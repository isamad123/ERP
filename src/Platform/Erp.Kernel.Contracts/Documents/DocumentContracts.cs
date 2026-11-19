using Erp.BuildingBlocks.Contracts.Security;

namespace Erp.Kernel.Contracts.Documents;

/// <summary>
/// Permissions of the file mechanism (slice S1.11c1; KRN-DOC-01 to KRN-DOC-04). Files follow their record (ruling Q23): the
/// permissions to see, add, replace and delete a file are the owning record's own read and update permissions, registered
/// with the owner type. Only the two rights below belong to the files themselves.
/// </summary>
public static class DocumentPermissions
{
    /// <summary>Put a file under legal hold or release it (ruling Q24).</summary>
    public const string LegalHold = "doc.files.legal_hold";

    /// <summary>List files past their retention that wait for an administrator's review (ruling Q24).</summary>
    public const string RetentionReview = "doc.files.retention_review";

    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(LegalHold, "doc", "permissions.doc.files.legal_hold", true),
        new(RetentionReview, "doc", "permissions.doc.files.retention_review", false),
    ];
}

/// <summary>Status values of <c>doc.files</c> (the data design lists them).</summary>
public static class FileStatuses
{
    public const string Uploading = "uploading";
    public const string Clean = "clean";
    public const string Infected = "infected";
    public const string Quarantined = "quarantined";
    public const string Deleted = "deleted";

    /// <summary>The <c>storage_key</c> of a file whose content retention destroyed (the row itself is kept).</summary>
    public const string PurgedKey = "purged";
}

/// <summary>Error titles of the file mechanism (localization keys, <c>Documents/Localization/documents.*.json</c>).</summary>
public static class DocumentErrors
{
    public const string Invalid = "errors.document.invalid";
    public const string NotFound = "errors.document.not_found";
    public const string Forbidden = "errors.access.forbidden";
    public const string UnknownOwner = "errors.document.owner_unknown";
    public const string TooLarge = "errors.document.too_large";
    public const string TypeNotAllowed = "errors.document.type_not_allowed";
    public const string Empty = "errors.document.empty";
    public const string NotReady = "errors.document.not_ready";
    public const string StorageFull = "errors.document.storage_full";
    public const string Held = "errors.document.legal_hold";
}

/// <summary>Events and security events of the file mechanism.</summary>
public static class DocumentEvents
{
    /// <summary>Outbox event: a document is about to expire (reminder to the uploader).</summary>
    public const string Expiring = "kernel.document.expiring";

    /// <summary>Security event: a scan found a virus.</summary>
    public const string Infected = "doc.file_infected";

    public const string LegalHoldSet = "doc.legal_hold_set";
    public const string LegalHoldReleased = "doc.legal_hold_released";
}

/// <summary>One stored file (a version of an attachment) as the API shows it.</summary>
public sealed record AttachmentInfo(
    Guid Id,
    string OwnerType,
    Guid OwnerId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Checksum,
    int VersionNo,
    Guid? PreviousFileId,
    string Status,
    string? DocumentType,
    DateOnly? ExpiresOn,
    bool IsSensitive,
    bool LegalHold,
    string? LegalHoldReason,
    bool Scanned,
    DateTimeOffset UploadedAt,
    Guid UploadedBy);

/// <summary>A file to store. <paramref name="ReplacesFileId"/> makes it the next version of that attachment.</summary>
public sealed record AttachmentUpload(
    string OwnerType,
    Guid OwnerId,
    string FileName,
    byte[] Content,
    string? DocumentType,
    DateOnly? ExpiresOn,
    bool IsSensitive,
    Guid? ReplacesFileId);

/// <summary>Content and name of a download.</summary>
public sealed record AttachmentContent(string FileName, string ContentType, byte[] Content);

/// <summary>A file past its retention that an administrator reviews.</summary>
public sealed record RetentionReviewItem(Guid FileId, string OwnerType, Guid OwnerId, string FileName, string DocumentType, DateOnly DueOn);
