using System.Text.Json.Nodes;

namespace Erp.Kernel.Application.Audit;

/// <summary>Filters of the change-log search (FR-KRN-AUD-04): user, entity, record, date range, action, source.</summary>
public sealed record ChangeQuery(Guid? UserId, string? EntityType, Guid? EntityId, DateTimeOffset? From, DateTimeOffset? To, string? Action, string? Source, string? Cursor, int Limit);

/// <summary>One change-log entry as an allowed viewer sees it: protected values decrypted, or hidden by field security.</summary>
public sealed record ChangeItem(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid? ActorUserId,
    string ActorKind,
    string EntityType,
    Guid EntityId,
    Guid? CompanyId,
    string Action,
    string? Source,
    Guid? CorrelationId,
    string? IpAddress,
    JsonArray Changes);

/// <summary>A page of results, newest first; <see cref="Next"/> is the cursor of the following page.</summary>
public sealed record AuditPage<T>(IReadOnlyList<T> Items, string? Next);

public sealed record AccessLogQuery(Guid? UserId, string? EntityType, Guid? EntityId, DateTimeOffset? From, DateTimeOffset? To, string? Cursor, int Limit);

public sealed record AccessLogItem(Guid Id, DateTimeOffset OccurredAt, Guid ActorUserId, string EntityType, Guid? EntityId, IReadOnlyList<string> Fields, string Action, int? RowCount, Guid? CorrelationId);

public sealed record SecurityEventQuery(string? EventType, string? Severity, string? Actor, string? Subject, DateTimeOffset? From, DateTimeOffset? To, string? Cursor, int Limit);

public sealed record SecurityEventItem(Guid Id, DateTimeOffset OccurredAt, string EventType, string? Actor, string? Subject, string Severity, JsonObject Details);

/// <summary>The CSV of an export and the number of rows in it (at most <see cref="AuditLimits.ExportRows"/>).</summary>
public sealed record AuditExport(string Csv, int Rows, bool Truncated);

/// <summary>
/// Result of a chain check (FR-KRN-AUD-03): <see cref="Intact"/> or the first broken row and what is wrong with it.
/// <see cref="Findings"/> lists every problem found (row changed, link broken, anchor mismatch, control-plane copy mismatch).
/// </summary>
public sealed record ChainVerification(
    DateOnly From,
    DateOnly To,
    bool Intact,
    long RowsChecked,
    int DaysChecked,
    Guid? BrokenRowId,
    DateTimeOffset? BrokenAt,
    string? Problem,
    bool ControlPlaneCompared,
    IReadOnlyList<string> Findings);

public static class AuditLimits
{
    /// <summary>Export cap per request in Release 1.0 (ruling Q10); larger exports become background jobs (S1.16).</summary>
    public const int ExportRows = 10_000;

    public const int DefaultPage = 50;

    public const int MaxPage = 200;

    /// <summary>Longest range one verification request may check (a year); longer checks run day by day as jobs (S1.11).</summary>
    public const int MaxVerifyDays = 366;
}

/// <summary>
/// Reading the audit log (FR-KRN-AUD-04, FR-KRN-AUD-07, ADM-USR-04). Values pass through the field security of the record's
/// table for the viewer; viewing decrypted personal values and exporting are written to the access log (ruling Q6).
/// </summary>
public interface IAuditQueryService
{
    Task<AuditPage<ChangeItem>> ChangesAsync(ChangeQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// One record's history, or null when the viewer may not read that record (no read permission, outside the data scope,
    /// or a table without its own read API and no <c>aud.changes.view</c>).
    /// </summary>
    Task<IReadOnlyList<ChangeItem>?> HistoryAsync(string entityType, Guid entityId, CancellationToken cancellationToken);

    Task<AuditPage<AccessLogItem>> AccessLogAsync(AccessLogQuery query, CancellationToken cancellationToken);

    Task<AuditPage<SecurityEventItem>> SecurityEventsAsync(SecurityEventQuery query, CancellationToken cancellationToken);

    /// <summary>The signed-in user's own sign-in history (ruling Q10: everyone may see theirs).</summary>
    Task<AuditPage<SecurityEventItem>> MySignInsAsync(string? cursor, int limit, CancellationToken cancellationToken);

    Task<AuditExport> ExportChangesAsync(ChangeQuery query, CancellationToken cancellationToken);
}

/// <summary>Recomputes the hash chain for a date range and names the first broken row (FR-KRN-AUD-03).</summary>
public interface IAuditVerifier
{
    Task<ChainVerification> VerifyAsync(DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken);
}
