using System.Text.Json;
using Erp.BuildingBlocks.Contracts.Api;
using Erp.Kernel.Application.Documents;
using Erp.Kernel.Contracts.Documents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Erp.Kernel.Api.Documents;

/// <summary>
/// The document template API (KRN-DOC-05, HRM-DOC-08; slice S1.11c2 Build item 20). Under <c>/api/doc/templates</c>: create,
/// versions (new draft, change draft, activate), retire, preview and produce a document for a record. The customer comes from
/// the host, never from the request. Previews and single documents are made in the request (ruling Q27); batches are the job
/// <c>kernel.doc_generate_batch</c>. The generated <c>/api/doc/*</c> endpoints of these tables only read (writes closed in the data design), so the list and detail of templates are the generated ones.
/// Error titles are localization keys (<c>documents.*.json</c>).
/// </summary>
public static class TemplateEndpoints
{
    private const string Templates = "doc.templates";

    public static IEndpointRouteBuilder MapTemplateEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var g = app.MapGroup("/api/doc/templates").WithTags("doc.templates");

        Tag(g.MapPost("/", (TemplateCreate body, ITemplateService s, CancellationToken ct) =>
            RunAsync(async () =>
            {
                var created = await s.CreateAsync(body, ct).ConfigureAwait(false);
                return Results.Created("/api/doc/templates/" + created.Id + "/versions", created);
            })), "catalog", TemplatePermissions.TemplatesCreate);
        Tag(g.MapPost("/preview", (TemplatePreviewRequest body, ITemplateService s, CancellationToken ct) =>
            RunAsync(async () =>
            {
                var preview = await s.PreviewAsync(body, ct).ConfigureAwait(false);
                return Results.Ok(new { preview.Html, Pdf = preview.Pdf is null ? null : Convert.ToBase64String(preview.Pdf), preview.UsedFields });
            })), "catalog", TemplatePermissions.TemplatesRead);
        Tag(g.MapPost("/generate", (GenerateRequest body, ITemplateService s, CancellationToken ct) =>
            RunAsync(async () =>
            {
                var made = await s.GenerateAsync(body, ct).ConfigureAwait(false);
                return Results.Created(made.FileId is { } file ? "/api/doc/attachments/" + file + "/content" : "/api/doc/templates/generated", made);
            })), "catalog", TemplatePermissions.GeneratedCreate);
        Tag(g.MapGet("/generated", (string? ownerType, Guid ownerId, ITemplateService s, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await s.GeneratedAsync(ownerType ?? string.Empty, ownerId, ct).ConfigureAwait(false)))), "catalog", TemplatePermissions.GeneratedRead);
        Tag(g.MapGet("/entity-fields", (ITemplateService s, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await s.EntityFieldsAsync(ct).ConfigureAwait(false)))), "catalog", TemplatePermissions.VersionsRead);
        Tag(g.MapGet("/{id:guid}/versions", (Guid id, ITemplateService s, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await s.VersionsAsync(id, ct).ConfigureAwait(false)))), "record_action", TemplatePermissions.VersionsRead);
        Tag(g.MapPost("/{id:guid}/versions", (Guid id, TemplateVersionEdit body, ITemplateService s, CancellationToken ct) =>
            RunAsync(async () => Results.Created("/api/doc/templates/" + id + "/versions", await s.NewVersionAsync(id, body, ct).ConfigureAwait(false)))), "record_action", TemplatePermissions.VersionsCreate);
        Tag(g.MapPost("/{id:guid}/retire", (Guid id, RowVersionRequest body, ITemplateService s, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await s.RetireAsync(id, body.RowVersion, ct).ConfigureAwait(false)))), "record_action", TemplatePermissions.TemplatesUpdate);
        Tag(g.MapPut("/versions/{versionId:guid}", (Guid versionId, VersionUpdate body, ITemplateService s, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await s.UpdateDraftAsync(versionId, new TemplateVersionEdit(body.Body, body.OutputFormat), body.RowVersion, ct).ConfigureAwait(false)))), "record_action", TemplatePermissions.VersionsUpdate);
        Tag(g.MapPost("/versions/{versionId:guid}/activate", (Guid versionId, RowVersionRequest body, ITemplateService s, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await s.ActivateAsync(versionId, body.RowVersion, ct).ConfigureAwait(false)))), "record_action", TemplatePermissions.VersionsUpdate);
        return app;
    }

    private static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (DocumentException ex)
        {
            return Results.Problem(statusCode: ex.Status, title: ex.Message, extensions: ex.Data.Contains("detail") ? new Dictionary<string, object?> { ["detail"] = ex.Data["detail"]?.ToString() } : null);
        }
        catch (JsonException)
        {
            return Results.Problem(statusCode: 400, title: TemplateErrors.Invalid);
        }
    }

    private static void Tag(RouteHandlerBuilder endpoint, string kind, string permission) =>
        endpoint.WithMetadata(new NotificationEndpointMetadata(kind, Templates, permission));

    /// <summary>Body of the retire and activate calls: the row version the caller saw.</summary>
    public sealed record RowVersionRequest(long RowVersion);

    /// <summary>Body of <c>PUT /api/doc/templates/versions/{versionId}</c>.</summary>
    public sealed record VersionUpdate(string Body, string? OutputFormat, long RowVersion);
}
