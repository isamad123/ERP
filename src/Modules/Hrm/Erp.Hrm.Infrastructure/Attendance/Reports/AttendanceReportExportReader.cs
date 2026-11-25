using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Contracts.Attendance;
using Erp.Kernel.Contracts.Itg;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Hrm.Infrastructure.Attendance.Reports;

/// <summary>
/// One standard attendance report as an exportable list (slice S1.30b, FR-HRM-ATT-19, FR-ADM-DAT-04): the export engine asks as the person who requested the
/// file, so the report's own permission and data scope apply row by row exactly as on screen. The filter of the export is the report's filter (the same names
/// as the URL); the cursor is the row offset. No report has a pay or salary column, so no column is hidden or masked.
/// </summary>
internal sealed class AttendanceReportExportReader(IServiceProvider services, string reportCode) : IExportRowReader
{
    public string Code => AttendanceReportCodes.ExportType(reportCode);

    public string ReadPermission => AttendanceReportPermissions.Reports;

    public Task<IReadOnlyList<(ExportColumn Column, string Access)>> ColumnsAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        var columns = services.GetRequiredService<IAttendanceReportService>().ColumnsOf(reportCode) ?? [];
        return Task.FromResult<IReadOnlyList<(ExportColumn, string)>>([.. columns.Select(c => (new ExportColumn(c.Key, c.JsonName, null, false), "visible"))]);
    }

    public async Task<ExportPage> ReadPageAsync(ExportQuery query, string? after, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var filter = query.Filter.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<AttendanceReportQuery>(query.Filter.GetRawText(), JsonSerializerOptions.Web) ?? new AttendanceReportQuery()
            : new AttendanceReportQuery();
        var search = filter.Search is { Length: > 0 } ? filter : new AttendanceReportQuery
        {
            CompanyId = filter.CompanyId, From = filter.From, To = filter.To, UnitId = filter.UnitId, BranchId = filter.BranchId, LocationId = filter.LocationId,
            EmploymentId = filter.EmploymentId, Search = query.Search, Status = filter.Status, Kind = filter.Kind, MinAgeDays = filter.MinAgeDays,
        };
        var offset = int.TryParse(after, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var o) ? o : 0;
        var page = await services.GetRequiredService<IAttendanceReportService>().ReadRowsAsync(reportCode, search, offset, limit, cancellationToken).ConfigureAwait(false);
        return page is { } p
            ? new ExportPage([.. p.Rows], p.Next?.ToString(System.Globalization.CultureInfo.InvariantCulture), [])
            : new ExportPage(new List<JsonObject>(), null, []);
    }
}
