using Erp.BuildingBlocks.Contracts.Api;
using Erp.Kernel.Contracts.Access;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Erp.Kernel.Api.Access;

/// <summary>
/// The API client endpoints (slice S1.27c Build item 10, FR-ADM-SEC-02), under <c>/api/iam/api-clients</c>: scope options, create (the secret is in the answer once,
/// never cached), list, read, rotate, revoke and usage. They are for people with <see cref="ApiClientPermissions.Manage"/> or <see cref="ApiClientPermissions.Read"/>;
/// an API key can never reach them (the key gate refuses everything under <c>/api/iam</c>). The customer comes from the host, never from the request.
/// </summary>
public static class ApiClientEndpoints
{
    private const string Resource = "iam.api_clients";

    public static IEndpointRouteBuilder MapApiClientEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var g = app.MapGroup("/api/iam/api-clients").WithTags(Resource);

        Tag(g.MapGet("/scope-options", (IApiClientService s, CancellationToken ct) => RunAsync(async () => Results.Ok(await s.ScopeOptionsAsync(ct).ConfigureAwait(false)))), "catalog", ApiClientPermissions.Read);
        Tag(g.MapGet("/", (IApiClientService s, CancellationToken ct) => RunAsync(async () => Results.Ok(await s.ListAsync(ct).ConfigureAwait(false)))), "catalog", ApiClientPermissions.Read);
        Tag(g.MapPost("/", (ApiClientCreateRequest body, HttpContext http, IApiClientService s, CancellationToken ct) => RunAsync(async () =>
        {
            var secret = await s.CreateAsync(body, ct).ConfigureAwait(false);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Json(secret, statusCode: StatusCodes.Status201Created);
        })), "create", ApiClientPermissions.Manage);
        Tag(g.MapGet("/{id:guid}", (Guid id, IApiClientService s, CancellationToken ct) => RunAsync(async () => Results.Ok(await s.GetAsync(id, ct).ConfigureAwait(false)))), "record_action", ApiClientPermissions.Read);
        Tag(g.MapPost("/{id:guid}/rotate", (Guid id, HttpContext http, IApiClientService s, CancellationToken ct) => RunAsync(async () =>
        {
            var secret = await s.RotateAsync(id, ct).ConfigureAwait(false);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(secret);
        })), "record_action", ApiClientPermissions.Manage);
        Tag(g.MapPost("/{id:guid}/revoke", (Guid id, IApiClientService s, CancellationToken ct) => RunAsync(async () => Results.Ok(await s.RevokeAsync(id, ct).ConfigureAwait(false)))), "record_action", ApiClientPermissions.Manage);
        Tag(g.MapGet("/{id:guid}/usage", (Guid id, [FromQuery] int? days, IApiClientService s, CancellationToken ct) => RunAsync(async () => Results.Ok(await s.UsageAsync(id, days ?? 30, ct).ConfigureAwait(false)))), "record_action", ApiClientPermissions.Read);
        return app;
    }

    private static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (ApiClientException e)
        {
            return Results.Problem(statusCode: e.Status, title: e.ErrorKey);
        }
    }

    private static void Tag(RouteHandlerBuilder endpoint, string kind, string permission) =>
        endpoint.WithMetadata(new AccessEndpointMetadata(kind, Resource, permission));
}
