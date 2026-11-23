using Erp.BuildingBlocks.Contracts.Api;
using Erp.Kernel.Application.Itg;
using Erp.Kernel.Contracts.Itg;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Erp.Kernel.Api.Itg;

/// <summary>
/// The import centre API (slice S1.16a, FR-ADM-DAT-01, FR-KRN-INT-03, -04, FR-ADM-INT-04), under <c>/api/itg/imports</c>: import types,
/// templates (list, create, change, template file, test with a sample file) and batches (upload, check, commit, roll back, problems).
/// The customer comes from the host, never from the request; who may import which type is the service's rule, and every row passes
/// through the writer of its table. Error titles are localization keys (<c>import.*.json</c>).
/// </summary>
public static class ImportEndpoints
{
    private const string Batches = "itg.import_batches";
    private const string Templates = "itg.import_templates";
    private const long MaxUploadBytes = 200L * 1024 * 1024;
    private const long FormOverheadBytes = 1024L * 1024;

    public static IEndpointRouteBuilder MapImportEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var g = app.MapGroup("/api/itg/imports").WithTags("itg.imports");

        Tag(g.MapGet("/types", (IImportService s, CancellationToken ct) => RunAsync(async () => Results.Ok(await s.TypesAsync(ct).ConfigureAwait(false)))),
            "catalog", Templates, ImportPermissions.Manage);
        Tag(g.MapGet("/templates", ([FromQuery] string? type, IImportService s, CancellationToken ct) => RunAsync(async () => Results.Ok(await s.TemplatesAsync(type, ct).ConfigureAwait(false)))),
            "catalog", Templates, ImportPermissions.Manage);
        Tag(g.MapPost("/templates", (ImportTemplateWrite body, IImportService s, CancellationToken ct) =>
            RunAsync(async () =>
            {
                var created = await s.CreateTemplateAsync(body, ct).ConfigureAwait(false);
                return Results.Created("/api/itg/imports/templates/" + created.Id, created);
            })), "create", Templates, ImportPermissions.Design);
        Tag(g.MapPut("/templates/{id:guid}", (Guid id, ImportTemplateWrite body, IImportService s, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await s.UpdateTemplateAsync(id, body, ct).ConfigureAwait(false)))), "record_action", Templates, ImportPermissions.Design);
        Tag(g.MapGet("/templates/{id:guid}/file", (Guid id, [FromQuery] string? format, IImportService s, CancellationToken ct) =>
            RunAsync(async () =>
            {
                var file = await s.TemplateFileAsync(id, format, ct).ConfigureAwait(false);
                return Results.File(file.Content, file.ContentType, file.FileName);
            })), "record_action", Templates, ImportPermissions.Manage);
        Tag(g.MapPost("/templates/{id:guid}/test", (Guid id, [FromForm] UploadForm form, IImportService s, CancellationToken ct) =>
            RunAsync(async () =>
            {
                var (name, content) = await ReadAsync(form, ct).ConfigureAwait(false);
                return Results.Ok(await s.TestTemplateAsync(id, name, content, ct).ConfigureAwait(false));
            })).DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(MaxUploadBytes + FormOverheadBytes), new RequestFormLimitsAttribute { MultipartBodyLengthLimit = MaxUploadBytes + FormOverheadBytes }),
            "record_action", Templates, ImportPermissions.Manage);

        Tag(g.MapPost("/batches", ([FromForm] UploadForm form, IImportService s, CancellationToken ct) =>
            RunAsync(async () =>
            {
                if (form.TemplateId == Guid.Empty)
                {
                    throw new ImportException("errors.import.not_found", 404);
                }

                var (name, content) = await ReadAsync(form, ct).ConfigureAwait(false);
                var batch = await s.UploadAsync(form.TemplateId, name, content, ct).ConfigureAwait(false);
                return Results.Created("/api/itg/imports/batches/" + batch.Id, batch);
            })).DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(MaxUploadBytes + FormOverheadBytes), new RequestFormLimitsAttribute { MultipartBodyLengthLimit = MaxUploadBytes + FormOverheadBytes }),
            "upload", Batches, ImportPermissions.Manage);
        Tag(g.MapGet("/batches", ([FromQuery] string? type, [FromQuery] string? status, [FromQuery] string? cursor, [FromQuery] int? limit, IImportService s, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await s.ListAsync(type, status, cursor, limit ?? 0, ct).ConfigureAwait(false)))), "catalog", Batches, ImportPermissions.Manage);
        Tag(g.MapGet("/batches/{id:guid}", (Guid id, IImportService s, CancellationToken ct) => RunAsync(async () => Results.Ok(await s.GetAsync(id, ct).ConfigureAwait(false)))),
            "record_action", Batches, ImportPermissions.Manage);
        Tag(g.MapPost("/batches/{id:guid}/validate", (Guid id, IImportService s, CancellationToken ct) =>
            RunAsync(async () => Results.Accepted("/api/itg/imports/batches/" + id, await s.ValidateAsync(id, ct).ConfigureAwait(false)))), "record_action", Batches, ImportPermissions.Manage);
        Tag(g.MapPost("/batches/{id:guid}/commit", (Guid id, CommitBody? body, IImportService s, CancellationToken ct) =>
            RunAsync(async () => Results.Accepted("/api/itg/imports/batches/" + id, await s.CommitAsync(id, body?.SkipErrors ?? false, ct).ConfigureAwait(false)))), "record_action", Batches, ImportPermissions.Manage);
        Tag(g.MapPost("/batches/{id:guid}/rollback", (Guid id, ReasonBody? body, IImportService s, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await s.RollbackAsync(id, body?.Reason, ct).ConfigureAwait(false)))), "record_action", Batches, ImportPermissions.Manage);
        Tag(g.MapGet("/batches/{id:guid}/errors", (Guid id, [FromQuery] int? limit, [FromQuery] string? cursor, IImportService s, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await s.ErrorsAsync(id, limit ?? 0, cursor, ct).ConfigureAwait(false)))), "record_action", Batches, ImportPermissions.Manage);
        Tag(g.MapGet("/batches/{id:guid}/errors.csv", (Guid id, IImportService s, CancellationToken ct) =>
            RunAsync(async () =>
            {
                var file = await s.ErrorFileAsync(id, ct).ConfigureAwait(false);
                return Results.File(file.Content, file.ContentType, file.FileName);
            })), "record_action", Batches, ImportPermissions.Manage);
        return app;
    }

    private static async Task<(string Name, byte[] Content)> ReadAsync(UploadForm form, CancellationToken ct)
    {
        if (form.File is null)
        {
            throw new ImportException("errors.import.empty_file", 400);
        }

        if (form.File.Length > MaxUploadBytes)
        {
            throw new ImportException("errors.import.too_large", 413);
        }

        await using var stream = form.File.OpenReadStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        return (form.File.FileName, buffer.ToArray());
    }

    private static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (ImportException e)
        {
            return Results.Problem(statusCode: e.Status, title: e.ErrorKey, detail: e.Detail);
        }
    }

    private static void Tag(RouteHandlerBuilder endpoint, string kind, string resource, string permission) =>
        endpoint.WithMetadata(new AccessEndpointMetadata(kind, resource, permission));

    /// <summary>The multipart form of an upload or a template test.</summary>
    public sealed class UploadForm
    {
        public IFormFile? File { get; init; }

        public Guid TemplateId { get; init; }
    }

    /// <summary>The optional body of a commit.</summary>
    public sealed record CommitBody(bool? SkipErrors);

    /// <summary>The optional body of a rollback: the person's note.</summary>
    public sealed record ReasonBody(string? Reason);
}
