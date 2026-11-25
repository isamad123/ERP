using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.Hrm.Application.Leave;
using Erp.Hrm.Contracts.Leave;
using Erp.Kernel.Contracts.Itg;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Hrm.Infrastructure.Leave.Reports;

/// <summary>
/// One standard leave report as an exportable list (slice S1.33b, FR-HRM-LVE-10, FR-ADM-DAT-04): the export engine asks as the person who requested the file, so the
/// report's own permission and data scope apply row by row exactly as on screen. The filter of the export is the report's filter (the same names as the URL); the
/// cursor is the row offset. No report has a pay or salary column, so no column is hidden or masked.
/// </summary>
internal sealed class LeaveReportExportReader(IServiceProvider services, string reportCode) : IExportRowReader
{
    public string Code => LeaveReportCodes.ExportType(reportCode);

    public string ReadPermission => LeavePermissions.ReportsRead;

    public Task<IReadOnlyList<(ExportColumn Column, string Access)>> ColumnsAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        var columns = services.GetRequiredService<ILeaveReportService>().ColumnsOf(reportCode) ?? [];
        return Task.FromResult<IReadOnlyList<(ExportColumn, string)>>([.. columns.Select(c => (new ExportColumn(c.Key, c.JsonName, null, false), "visible"))]);
    }

    public async Task<ExportPage> ReadPageAsync(ExportQuery query, string? after, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var filter = query.Filter.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<LeaveReportQuery>(query.Filter.GetRawText(), JsonSerializerOptions.Web) ?? new LeaveReportQuery()
            : new LeaveReportQuery();
        var search = filter.Search is { Length: > 0 } ? filter : new LeaveReportQuery
        {
            CompanyId = filter.CompanyId, UnitId = filter.UnitId, EmploymentId = filter.EmploymentId, LeaveTypeId = filter.LeaveTypeId, LeaveYear = filter.LeaveYear,
            From = filter.From, To = filter.To, Search = query.Search, Status = filter.Status,
        };
        var offset = int.TryParse(after, NumberStyles.None, CultureInfo.InvariantCulture, out var o) ? o : 0;
        var page = await services.GetRequiredService<ILeaveReportService>().ReadRowsAsync(reportCode, search, offset, limit, cancellationToken).ConfigureAwait(false);
        return page is { } p
            ? new ExportPage([.. p.Rows], p.Next?.ToString(CultureInfo.InvariantCulture), [])
            : new ExportPage(new List<JsonObject>(), null, []);
    }
}
