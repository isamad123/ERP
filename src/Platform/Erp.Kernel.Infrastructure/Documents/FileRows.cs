using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.Kernel.Contracts.Documents;
using Erp.Kernel.Domain.Settings;
using Erp.Kernel.Infrastructure.Audit;
using Erp.Kernel.Infrastructure.Settings;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Documents;

/// <summary>One <c>doc.files</c> row as the file mechanism reads it.</summary>
public sealed record FileRow(
    Guid Id,
    Guid CompanyId,
    string OwnerType,
    Guid OwnerId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Checksum,
    string StorageKey,
    int VersionNo,
    Guid? PreviousFileId,
    string? DocumentType,
    DateOnly? ExpiresOn,
    DateOnly? RetentionUntil,
    bool IsSensitive,
    string Status,
    bool LegalHold,
    string? LegalHoldReason,
    DateTimeOffset? ScannedAt,
    DateTimeOffset? ArchivedAt,
    DateTimeOffset CreatedAt,
    Guid CreatedBy)
{
    public AttachmentInfo ToInfo() => new(Id, OwnerType, OwnerId, FileName, ContentType, SizeBytes, Checksum, VersionNo, PreviousFileId, Status, DocumentType, ExpiresOn, IsSensitive,
        LegalHold, LegalHoldReason, ScannedAt is not null, CreatedAt, CreatedBy);
}

/// <summary>The SQL of the file mechanism: every value is a parameter (spec 03, parameterized SQL only).</summary>
public static class FileRows
{
    public const string Columns = """
        f.id, f.company_id, f.owner_type, f.owner_id, f.file_name, f.content_type, f.size_bytes, f.checksum, f.storage_key, f.version_no, f.previous_file_id,
        f.document_type, f.expires_on, f.retention_until, f.is_sensitive, f.status, f.legal_hold, f.legal_hold_reason, f.scanned_at, f.archived_at, f.created_at, f.created_by
        """;

    /// <summary>A file with no newer, not deleted version.</summary>
    public const string IsHead = "not exists (select 1 from doc.files n where n.previous_file_id = f.id and n.status <> 'deleted')";

    public static FileRow Read(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetGuid(3), r.GetString(4), r.GetString(5), r.GetInt64(6), r.GetString(7), r.GetString(8), r.GetInt32(9),
        r.IsDBNull(10) ? null : r.GetGuid(10), r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetFieldValue<DateOnly>(12), r.IsDBNull(13) ? null : r.GetFieldValue<DateOnly>(13),
        r.GetBoolean(14), r.GetString(15), r.GetBoolean(16), r.IsDBNull(17) ? null : r.GetString(17), r.IsDBNull(18) ? null : r.GetFieldValue<DateTimeOffset>(18),
        r.IsDBNull(19) ? null : r.GetFieldValue<DateTimeOffset>(19), r.GetFieldValue<DateTimeOffset>(20), r.GetGuid(21));

    public static async Task<FileRow?> FindAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid id, CancellationToken ct) =>
        (await Notifications.NotificationConsumer.ReadAsync(c, tx, $"select {Columns} from doc.files f where f.id = $1", Read, ct, id).ConfigureAwait(false)).SingleOrDefault();

    /// <summary>The ids of every version of the attachment <paramref name="id"/> belongs to (older and newer).</summary>
    public static Task<List<Guid>> ChainAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid id, CancellationToken ct) =>
        Notifications.NotificationConsumer.ReadAsync(c, tx, """
            with recursive older as (
                select id, previous_file_id from doc.files where id = $1
                union all select p.id, p.previous_file_id from doc.files p join older o on o.previous_file_id = p.id),
            newer as (
                select id from doc.files where id = $1
                union all select n.id from doc.files n join newer w on n.previous_file_id = w.id)
            select id from older union select id from newer
            """, r => r.GetGuid(0), ct, id);

    /// <summary>Writes one change-log entry of a file (every change is audited).</summary>
    public static Task LogAsync(NpgsqlConnection c, NpgsqlTransaction tx, FileRow file, string action, IReadOnlyList<(string Field, object? Before, object? After)> changes,
        Guid actor, string actorKind, string source, Guid? correlation, string? ip, DateTimeOffset now, CancellationToken ct)
    {
        var array = new JsonArray();
        foreach (var (field, before, after) in changes)
        {
            var node = new JsonObject { ["field"] = field };
            if (before is not null)
            {
                node["before"] = JsonSerializer.SerializeToNode(before);
            }

            node["after"] = after is null ? null : JsonSerializer.SerializeToNode(after);
            array.Add(node);
        }

        return AuditDb.AppendChangesAsync(c, tx, [new ChangeEntry(Guid.CreateVersion7(now), now, actor, actorKind, "doc.files", file.Id, file.CompanyId, action, array, null, correlation, ip, source, actor)], ct);
    }
}

/// <summary>The settings the file mechanism reads for a company (rulings Q21, Q22, Q25).</summary>
public sealed record DocumentSettings(bool ScanRequired, long MaxBytes, IReadOnlySet<string> AllowedTypes, IReadOnlyList<int> ReminderDays)
{
    public static async Task<DocumentSettings> LoadAsync(NpgsqlConnection c, Guid? companyId, Guid? userId, DateOnly today, CancellationToken ct)
    {
        var values = await SettingsStore.ResolveAsync(c, null, [Contracts.Generated.KernelSettingKeys.DocScanMode, Contracts.Generated.KernelSettingKeys.DocUploadMaxMb,
            Contracts.Generated.KernelSettingKeys.DocUploadAllowedTypes, Contracts.Generated.KernelSettingKeys.DocExpiryReminderDays], companyId, userId, today, ct).ConfigureAwait(false);
        var scan = values.TryGetValue(Contracts.Generated.KernelSettingKeys.DocScanMode, out var s) && s.Value.ValueKind == JsonValueKind.String ? s.Value.GetString() : "required";
        var mb = values.TryGetValue(Contracts.Generated.KernelSettingKeys.DocUploadMaxMb, out var m) && m.Value.TryGetInt32(out var n) && n > 0 ? n : 20;
        var types = Texts(values, Contracts.Generated.KernelSettingKeys.DocUploadAllowedTypes, FileValidation.DefaultAllowed.Split(','));
        var days = Texts(values, Contracts.Generated.KernelSettingKeys.DocExpiryReminderDays, ["30", "7", "0"])
            .Select(t => int.TryParse(t, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : -1).Where(d => d >= 0).Distinct().Order().ToList();
        return new DocumentSettings(!string.Equals(scan, "off", StringComparison.Ordinal), mb * 1024L * 1024, types.ToHashSet(StringComparer.Ordinal), days);
    }

    private static string[] Texts(Dictionary<string, ResolvedSetting> values, string key, string[] fallback) =>
        values.TryGetValue(key, out var v) && v.Value.ValueKind == JsonValueKind.Array ? [.. v.Value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)] : fallback;
}
