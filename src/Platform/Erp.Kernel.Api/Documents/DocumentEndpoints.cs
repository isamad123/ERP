using Erp.BuildingBlocks.Contracts.Api;
using Erp.Kernel.Application.Documents;
using Erp.Kernel.Contracts.Documents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Erp.Kernel.Api.Documents;

/// <summary>
/// The attachment API (KRN-DOC-01 to KRN-DOC-04, KRN-WFL-07; slice S1.11c1 Build items 17 to 19). Under
/// <c>/api/doc/attachments</c>: upload (multipart form), list per record, detail, versions, download, restore, delete and
/// legal hold. Who may do what follows the record the file belongs to (ruling Q23); the customer comes from the host, never
/// from the request. <c>doc.files</c> has no generated endpoints (CR-051, ruling Q23): files are read only here, through their
/// record. Error titles are localization keys (<c>documents.*.json</c>).
/// </summary>
public static class DocumentEndpoints
{
    private const string Files = "doc.files";

    /// <summary>The isolation inventory's permission label: these endpoints have no permission of their own, the owning record decides (ruling Q23).</summary>
    private const string OwnerDecides = "owner";

    /// <summary>
    /// The largest upload the API accepts at all: the ceiling of the setting <c>doc.upload.max_mb</c> (200 MB, settings.yaml),
    /// so the web server's default request limit (about 28.6 MB) never refuses a file the customer's setting allows
    /// (S1.11c1 review finding 4). The customer's own, lower limit is checked by the attachment service.
    /// </summary>
    public const long MaxUploadBytes = 200L * 1024 * 1024;

    /// <summary>Room for the multipart envelope and the other form fields around the file.</summary>
    private const long FormOverheadBytes = 1024L * 1024;

    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var g = app.MapGroup("/api/doc/attachments").WithTags("doc.attachments");

        Tag(g.MapPost("/", ([FromForm] UploadForm form, IAttachmentService s, CancellationToken ct) =>
            RunAsync(async () =>
            {
                if (form.File is null || string.IsNullOrWhiteSpace(form.OwnerType) || form.OwnerId == Guid.Empty)
                {
                    throw new DocumentException(DocumentErrors.Invalid);
                }

                if (form.File.Length > MaxUploadBytes)
                {
                    throw new DocumentException(DocumentErrors.TooLarge, 413); // refused before the content is copied into memory
                }

                await using var stream = form.File.OpenReadStream();
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
                var info = await s.UploadAsync(new AttachmentUpload(form.OwnerType, form.OwnerId, form.File.FileName, buffer.ToArray(), form.DocumentType, form.ExpiresOn, form.IsSensitive ?? false, form.ReplacesFileId), ct).ConfigureAwait(false);
                return Results.Created("/api/doc/attachments/" + info.Id, info);
            }))
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(MaxUploadBytes + FormOverheadBytes), new RequestFormLimitsAttribute { MultipartBodyLengthLimit = MaxUploadBytes + FormOverheadBytes }),
            "catalog", OwnerDecides);
        Tag(g.MapGet("/", ([AsParameters] ListParameters q, IAttachmentService s, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await s.ListAsync(q.OwnerType ?? string.Empty, q.OwnerId, ct).ConfigureAwait(false)))), "catalog", OwnerDecides);
        Tag(g.MapGet("/retention-review", (IAttachmentService s, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await s.RetentionReviewAsync(ct).ConfigureAwait(false)))), "catalog", DocumentPermissions.RetentionReview);
        Tag(g.MapGet("/{id:guid}", (Guid id, IAttachmentService s, CancellationToken ct) =>
            RunAsync(async () => await s.GetAsync(id, ct).ConfigureAwait(false) is { } found ? Results.Ok(found) : throw new DocumentException(DocumentErrors.NotFound, 404))), "record_action", OwnerDecides);
        Tag(g.MapGet("/{id:guid}/versions", (Guid id, IAttachmentService s, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await s.VersionsAsync(id, ct).ConfigureAwait(false)))), "record_action", OwnerDecides);
        Tag(g.MapGet("/{id:guid}/content", (Guid id, IAttachmentService s, CancellationToken ct) =>
            RunAsync(async () =>
            {
                var file = await s.DownloadAsync(id, ct).ConfigureAwait(false);
                return Results.File(file.Content, file.ContentType, file.FileName);
            })), "record_action", OwnerDecides);
        Tag(g.MapPost("/{id:guid}/restore", (Guid id, IAttachmentService s, CancellationToken ct) =>
            RunAsync(async () =>
            {
                var info = await s.RestoreAsync(id, ct).ConfigureAwait(false);
                return Results.Created("/api/doc/attachments/" + info.Id, info);
            })), "record_action", OwnerDecides);
        Tag(g.MapDelete("/{id:guid}", (Guid id, IAttachmentService s, CancellationToken ct) =>
            RunAsync(async () =>
            {
                await s.DeleteAsync(id, ct).ConfigureAwait(false);
                return Results.NoContent();
            })), "record_action", OwnerDecides);
        Tag(g.MapPut("/{id:guid}/legal-hold", (Guid id, LegalHoldRequest body, IAttachmentService s, CancellationToken ct) =>
            RunAsync(async () =>
            {
                await s.SetLegalHoldAsync(id, body.Hold, body.Reason, ct).ConfigureAwait(false);
                return Results.NoContent();
            })), "record_action", DocumentPermissions.LegalHold);
        return app;
    }

    /// <summary>Refusals become problem answers with their status (400, 403, 404, 409, 413, 415).</summary>
    private static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (DocumentException ex)
        {
            return Results.Problem(statusCode: ex.Status, title: ex.Message);
        }
    }

    private static void Tag(RouteHandlerBuilder endpoint, string kind, string permission) =>
        endpoint.WithMetadata(new NotificationEndpointMetadata(kind, Files, permission));

    /// <summary>The multipart form of an upload.</summary>
    public sealed class UploadForm
    {
        public string? OwnerType { get; init; }

        public Guid OwnerId { get; init; }

        public IFormFile? File { get; init; }

        public string? DocumentType { get; init; }

        public DateOnly? ExpiresOn { get; init; }

        public bool? IsSensitive { get; init; }

        public Guid? ReplacesFileId { get; init; }
    }

    /// <summary>Query string of <c>GET /api/doc/attachments</c>.</summary>
    public sealed class ListParameters
    {
        public string? OwnerType { get; init; }

        public Guid OwnerId { get; init; }
    }

    /// <summary>Body of <c>PUT /api/doc/attachments/{id}/legal-hold</c>.</summary>
    public sealed record LegalHoldRequest(bool Hold, string? Reason);
}
