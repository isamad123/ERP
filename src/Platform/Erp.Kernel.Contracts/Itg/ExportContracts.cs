using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Contracts.Security;

namespace Erp.Kernel.Contracts.Itg;

/// <summary>Permissions of the export centre (slice S1.16b, FR-ADM-DAT-04). Every export also needs the read permission of what it exports.</summary>
public static class ExportPermissions
{
    /// <summary>Start exports of lists the person may read, and see and download one's own exports.</summary>
    public const string Run = "itg.exports.run";

    /// <summary>Approve or refuse an export held for a second person (FR-KRN-IAM-14).</summary>
    public const string Approve = "itg.exports.approve";

    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(Run, "itg", "permissions.itg.exports.run", false),
        new(Approve, "itg", "permissions.itg.exports.approve", false),
    ];
}

/// <summary>Job code and held-change kind of the export engine.</summary>
public static class ExportCodes
{
    public const string RunJob = "kernel.export_run";
    public const string ExpireJob = "kernel.export_expire";
    public const string HeldKind = "itg.export.run";
    public const string OwnerType = "itg.export_jobs";
}

/// <summary>
/// What a list export reads: the list's own filters as the list endpoint takes them (JSON object of its query string), the
/// search text and the sort key. The same values give the same rows as the list.
/// </summary>
public sealed record ExportQuery(JsonElement Filter, string? Search, string? Sort);

/// <summary>One column of an exportable list: its field key, its name in the list's JSON answer and its field class (salary, personal, restricted) or null.</summary>
public sealed record ExportColumn(string Key, string JsonName, string? FieldClass, bool IsCustom);

/// <summary>A page of an export: rows shaped for the caller (hidden fields left out, masked fields masked), the cursor of the next page and the sensitive field keys shown unmasked.</summary>
public sealed record ExportPage(IReadOnlyList<JsonObject> Rows, Guid? Next, IReadOnlyList<string> SensitiveShown);

/// <summary>
/// Reads one list for an export (slice S1.16b). The generator emits one per generated table, nested in its endpoints class so it
/// uses the list endpoint's own filter, sort, search, data scope and field security; a module adds hand-written readers for lists
/// it builds itself. The caller is whoever the host's access services say (the export job runs as the person who asked).
/// </summary>
public interface IExportRowReader
{
    /// <summary>The list's code: the table (<c>hrm.employees</c>) or a hand-written list's own code.</summary>
    string Code { get; }

    /// <summary>The permission that lets the caller read this list.</summary>
    string ReadPermission { get; }

    /// <summary>The columns of the list with the access the caller has to each (<c>hidden</c>, <c>masked</c>, <c>visible</c>) for the company.</summary>
    Task<IReadOnlyList<(ExportColumn Column, string Access)>> ColumnsAsync(Guid? companyId, CancellationToken cancellationToken);

    /// <summary>One page of at most <paramref name="limit"/> rows after the row <paramref name="after"/>.</summary>
    Task<ExportPage> ReadPageAsync(ExportQuery query, Guid? after, int limit, CancellationToken cancellationToken);
}
