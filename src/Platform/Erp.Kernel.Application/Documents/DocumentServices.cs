using Erp.Kernel.Contracts.Documents;

namespace Erp.Kernel.Application.Documents;

/// <summary>
/// What the signed-in person may do with the files of one record (ruling Q23): read it and its files, change them, and read
/// files of a sensitive kind. The owner (a module, or the kernel for workflow tasks) decides, including data scopes.
/// </summary>
public sealed record AttachmentOwnerAccess(Guid CompanyId, bool CanRead, bool CanWrite, bool CanReadSensitive);

/// <summary>
/// A kind of record that can carry files (KRN-DOC-01). Modules register theirs with the kernel; the owner type is the table
/// name (for example <c>wf.workflow_tasks</c>). Returns null when the record does not exist.
/// </summary>
public interface IAttachmentOwner
{
    string OwnerType { get; }

    Task<AttachmentOwnerAccess?> AccessAsync(Guid ownerId, CancellationToken cancellationToken);
}

/// <summary>Attachments of records: add, replace, list, download, versions, restore, delete and legal hold (KRN-DOC-01 to KRN-DOC-04).</summary>
public interface IAttachmentService
{
    Task<AttachmentInfo> UploadAsync(AttachmentUpload upload, CancellationToken cancellationToken);

    /// <summary>The current (latest, not deleted, not archived) files of a record.</summary>
    Task<IReadOnlyList<AttachmentInfo>> ListAsync(string ownerType, Guid ownerId, CancellationToken cancellationToken);

    Task<AttachmentInfo?> GetAsync(Guid fileId, CancellationToken cancellationToken);

    /// <summary>The versions of the attachment the file belongs to, newest first.</summary>
    Task<IReadOnlyList<AttachmentInfo>> VersionsAsync(Guid fileId, CancellationToken cancellationToken);

    Task<AttachmentContent> DownloadAsync(Guid fileId, CancellationToken cancellationToken);

    /// <summary>Makes an earlier version the current one again, as a new version.</summary>
    Task<AttachmentInfo> RestoreAsync(Guid fileId, CancellationToken cancellationToken);

    /// <summary>Hides the attachment (every version); the content stays until retention removes it.</summary>
    Task DeleteAsync(Guid fileId, CancellationToken cancellationToken);

    Task SetLegalHoldAsync(Guid fileId, bool hold, string? reason, CancellationToken cancellationToken);

    Task<IReadOnlyList<RetentionReviewItem>> RetentionReviewAsync(CancellationToken cancellationToken);
}

/// <summary>A refusal of the file mechanism with the HTTP status it is answered with.</summary>
public sealed class DocumentException : Exception
{
    public DocumentException(string errorKey, int status = 400)
        : base(errorKey)
    {
        Status = status;
    }

    public DocumentException()
        : this(DocumentErrors.Invalid)
    {
    }

    public DocumentException(string message)
        : this(message, 400)
    {
    }

    public DocumentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public int Status { get; } = 400;
}
