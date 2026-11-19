using Erp.Kernel.Contracts.Documents;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Documents;

/// <summary>
/// Retention (slice S1.11c1 Build item 19; KRN-DOC-04, ruling Q24): the end of a file's retention is the date the owning
/// module set, or the upload date plus the rule's months; a country pack's minimum can never shorten it. Files of a type
/// without a rule are never touched, and a file under legal hold is never due.
/// </summary>
public static class Retention
{
    public sealed record DueFile(FileRow File, DateOnly DueOn, string Action);

    /// <summary>The files whose retention ended and whose rule's action is <paramref name="action"/>.</summary>
    public static async Task<IReadOnlyList<DueFile>> DueAsync(NpgsqlConnection c, string action, DateTimeOffset now, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var rows = await Notifications.NotificationConsumer.ReadAsync(c, null, $"""
            select {FileRows.Columns},
                greatest(coalesce(f.retention_until, (f.created_at at time zone 'UTC')::date + (r.keep_months * interval '1 month')::interval)::date,
                         (f.created_at at time zone 'UTC')::date + (coalesce(r.legal_minimum_months, 0) * interval '1 month')::interval)::date as due_on
            from doc.files f join doc.retention_rules r on r.document_type = f.document_type
            where r.action_after = $1 and not f.legal_hold and f.storage_key <> $2
              and (f.status <> 'deleted' or $1 = 'delete')
              and ($1 <> 'archive' or f.archived_at is null)
            """, r => (File: FileRows.Read(r), DueOn: DateOnly.FromDateTime(r.GetDateTime(22))), ct, action, FileStatuses.PurgedKey).ConfigureAwait(false);
        return [.. rows.Where(r => r.DueOn <= today).Select(r => new DueFile(r.File, r.DueOn, action))];
    }
}
