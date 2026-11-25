using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.Kernel.Application.Audit;
using Erp.Kernel.Contracts.Audit;
using Erp.Kernel.Contracts.Itg;

namespace Erp.Kernel.Infrastructure.Itg;

/// <summary>
/// The change log as an exportable list (slice S1.16b, FR-KRN-AUD-04, Build item 8: "the audit search's large exports as jobs"): the same
/// search, the same field security and access logging as the audit screen, read page by page without the 10,000-row cap of the quick export
/// (<c>/api/aud/export</c>). The <c>changes</c> column holds before and after values, so it is a restricted field class: a reason is asked.
/// </summary>
public sealed class AuditExportReader(IServiceProvider services) : IExportRowReader
{
    public const string Source = "aud.change_log";

    private static readonly ExportColumn[] Columns =
    [
        new("occurred_at", "occurredAt", null, false), new("actor_user_id", "actorUserId", null, false), new("actor_name", "actorName", null, false), new("actor_kind", "actorKind", null, false),
        new("entity_type", "entityType", null, false), new("entity_id", "entityId", null, false), new("company_id", "companyId", null, false), new("action", "action", null, false),
        new("source", "source", null, false), new("correlation_id", "correlationId", null, false), new("ip_address", "ipAddress", null, false), new("changes", "changes", "restricted", false),
    ];

    public string Code => Source;

    public string ReadPermission => AuditPermissions.Export;

    public Task<IReadOnlyList<(ExportColumn Column, string Access)>> ColumnsAsync(Guid? companyId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<(ExportColumn, string)>>([.. Columns.Select(c => (c, "visible"))]);

    public async Task<ExportPage> ReadPageAsync(ExportQuery query, string? after, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var f = query.Filter;
        string? Text(string name) => f.ValueKind == JsonValueKind.Object && f.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        Guid? Id(string name) => Guid.TryParse(Text(name), out var g) ? g : null;
        DateTimeOffset? Date(string name) => DateTimeOffset.TryParse(Text(name), null, System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;
        // Resolved when used: a host that never exports the audit log need not register the audit query service.
        var audit = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IAuditQueryService>(services);
        var page = await audit.ChangesAsync(new ChangeQuery(Id("userId"), Text("entityType"), Id("entityId"), Date("from"), Date("to"), Text("action"), Text("source"), after, Math.Clamp(limit, 1, AuditLimits.MaxPage)), cancellationToken).ConfigureAwait(false);
        var rows = page.Items.Select(i => new JsonObject
        {
            ["occurredAt"] = i.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["actorUserId"] = i.ActorUserId?.ToString(),
            ["actorName"] = i.ActorName,
            ["actorKind"] = i.ActorKind,
            ["entityType"] = i.EntityType,
            ["entityId"] = i.EntityId.ToString(),
            ["companyId"] = i.CompanyId?.ToString(),
            ["action"] = i.Action,
            ["source"] = i.Source,
            ["correlationId"] = i.CorrelationId?.ToString(),
            ["ipAddress"] = i.IpAddress,
            ["changes"] = i.Changes.ToJsonString(),
        }).ToList<JsonObject>();
        return new ExportPage(rows, page.Next, []);
    }
}
