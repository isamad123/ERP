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
/// from the request. The generated <c>/api/doc/files</c> endpoints only read. Error titles are localization keys (<c>documents.*.json</c>).
/// </summary>
public static class DocumentEndpoints
{
    private const string Files = "doc.files";

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

                await using var stream = form.File.OpenReadStream();
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
                var info = await s.UploadAsync(new AttachmentUpload(form.OwnerType, form.OwnerId, form.File.FileName, buffer.ToArray(), form.DocumentType, form.ExpiresOn, form.IsSensitive ?? false, form.ReplacesFileId), ct).ConfigureAwait(false);
                return Results.Created("/api/doc/attachments/" + info.Id, info);
            })).DisableAntiforgery(), "catalog", "doc.files.read");
        Tag(g.MapGet("/", ([AsParameters] ListParameters q, IAttachmentService s, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await s.ListAsync(q.OwnerType ?? string.Empty, q.OwnerId, ct).ConfigureAwait(false)))), "catalog", "doc.files.read");
        Tag(g.MapGet("/retention-review", (IAttachmentService s, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await s.RetentionReviewAsync(ct).ConfigureAwait(false)))), "catalog", DocumentPermissions.RetentionReview);
        Tag(g.MapGet("/{id:guid}", (Guid id, IAttachmentService s, CancellationToken ct) =>
            RunAsync(async () => await s.GetAsync(id, ct).ConfigureAwait(false) is { } found ? Results.Ok(found) : throw new DocumentException(DocumentErrors.NotFound, 404))), "record_action", "doc.files.read");
        Tag(g.MapGet("/{id:guid}/versions", (Guid id, IAttachmentService s, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await s.VersionsAsync(id, ct).ConfigureAwait(false)))), "record_action", "doc.files.read");
        Tag(g.MapGet("/{id:guid}/content", (Guid id, IAttachmentService s, CancellationToken ct) =>
            RunAsync(async () =>
            {
                var file = await s.DownloadAsync(id, ct).ConfigureAwait(false);
                return Results.File(file.Content, file.ContentType, file.FileName);
            })), "record_action", "doc.files.read");
        Tag(g.MapPost("/{id:guid}/restore", (Guid id, IAttachmentService s, CancellationToken ct) =>
            RunAsync(async () =>
            {
                var info = await s.RestoreAsync(id, ct).ConfigureAwait(false);
                return Results.Created("/api/doc/attachments/" + info.Id, info);
            })), "record_action", "doc.files.read");
        Tag(g.MapDelete("/{id:guid}", (Guid id, IAttachmentService s, CancellationToken ct) =>
            RunAsync(async () =>
            {
                await s.DeleteAsync(id, ct).ConfigureAwait(false);
                return Results.NoContent();
            })), "record_action", "doc.files.read");
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
