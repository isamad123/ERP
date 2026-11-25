using System.Text.Json;

namespace Erp.Kernel.Application.Itg;

/// <summary>One column the person wants in the file: the list's field name (JSON name, or <c>custom.&lt;key&gt;</c>) and the heading as the screen shows it.</summary>
public sealed record ExportColumnRequest(string? Field, string? Label);

/// <summary>
/// An export of one list (slice S1.16b, FR-ADM-DAT-04): the list (its table), the list's filters as its query string takes them, search,
/// sort, the columns in order, the format (<c>csv</c> or <c>xlsx</c>), the reason (needed when a sensitive field class is in it), the company
/// in hand (the export is filed under it) and the language of the headings and of two-language names.
/// </summary>
public sealed record ExportRequest(string? Source, JsonElement? Query, string? Search, string? Sort, IReadOnlyList<ExportColumnRequest>? Columns, string? Format, string? Reason, Guid? CompanyId, string? Language);

/// <summary>What an export would be, before it is asked for: the sensitive classes in it, whether it needs a reason and a second person, and the rows counted up to one more than the approval limit.</summary>
public sealed record ExportCheck(string Source, IReadOnlyList<string> SensitiveClasses, bool ReasonRequired, bool NeedsApproval, bool ApprovalOn, int Rows, bool RowsCapped, int MaxRows, int ApprovalRows, IReadOnlyList<string> Hidden, IReadOnlyList<string> Columns);

/// <summary>One export as the export centre shows it. <see cref="Status"/>: queued, running, done, failed, expired, awaiting_approval, rejected.</summary>
public sealed record ExportView(Guid Id, string Source, string Format, string Status, string? FileName, int? RowCount, int? SizeBytes, string? Reason, IReadOnlyList<string> SensitiveClasses,
    Guid CompanyId, Guid CreatedBy, string? CreatedByName, DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt, DateTimeOffset? ExpiresAt, string? ErrorKey, Guid? HeldRequestId, bool Mine, bool CanDownload, decimal? ProgressPct);

/// <summary>A page of exports; <see cref="Next"/> is passed back as the cursor.</summary>
public sealed record ExportPageResult(IReadOnlyList<ExportView> Items, string? Next);

/// <summary>A finished export file.</summary>
public sealed record ExportFile(byte[] Content, string ContentType, string FileName);

/// <summary>A refusal of the export centre with the HTTP status of its answer; <see cref="ErrorKey"/> is a localization key (<c>errors.export.*</c>).</summary>
public sealed class ExportException(string errorKey, int status = 400, string? detail = null) : Exception(errorKey)
{
    public string ErrorKey { get; } = errorKey;

    public int Status { get; } = status;

    public string? Detail { get; } = detail;
}

/// <summary>The export centre (slice S1.16b).</summary>
public interface IExportService
{
    Task<ExportCheck> CheckAsync(ExportRequest request, CancellationToken cancellationToken);

    /// <summary>Asks for an export: it is queued as a background job as the person who asked, or held for a second person (FR-KRN-IAM-14).</summary>
    Task<ExportView> StartAsync(ExportRequest request, CancellationToken cancellationToken);

    /// <summary>The person's own exports; with <paramref name="all"/>, those of the companies the person may read export logs of too.</summary>
    Task<ExportPageResult> ListAsync(bool all, string? status, string? cursor, int limit, CancellationToken cancellationToken);

    Task<ExportView> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The file of one's own finished export; checked again against the person's right to read the list.</summary>
    Task<ExportFile> DownloadAsync(Guid id, CancellationToken cancellationToken);
}
