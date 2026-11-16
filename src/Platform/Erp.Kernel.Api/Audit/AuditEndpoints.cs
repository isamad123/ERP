using System.Globalization;
using System.Text;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Contracts.Api;
using Erp.Kernel.Application.Audit;
using Erp.Kernel.Contracts.Audit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Erp.Kernel.Api.Audit;

/// <summary>Body of <c>POST /api/aud/verify</c>: the first and last day (UTC) to check.</summary>
public sealed record VerifyRequest(DateOnly? From, DateOnly? To);

/// <summary>
/// Audit endpoints (slice S1.07 build item 6; FR-KRN-AUD-02/03/04/07, ADM-USR-04): change-log search, one record's history,
/// access log, security events, the user's own sign-ins, CSV export (logged) and the chain check. Every value passes
/// through the viewer's field security. Error titles are localization keys (<c>Audit/Localization/audit.*.json</c>).
/// </summary>
public static class AuditEndpoints
{
    private static readonly string[] Actions = ["create", "update", "delete", "status"];
    private static readonly string[] Sources = ["ui", "api", "import", "job", "system"];
    private static readonly string[] SeverityValues = ["info", "warning", "critical"];

    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var aud = app.MapGroup("/api/aud").WithTags("aud");

        Tag(aud.MapGet("/changes", (
                [FromQuery] Guid? userId, [FromQuery] string? entityType, [FromQuery] Guid? entityId, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
                [FromQuery] string? action, [FromQuery] string? source, [FromQuery] string? cursor, [FromQuery] int? limit,
                IAuditQueryService s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, AuditPermissions.ViewChanges, async () =>
                ValidChange(action, source) is { } bad ? bad
                    : Results.Ok(await s.ChangesAsync(new ChangeQuery(userId, entityType, entityId, from, to, action, source, cursor, limit ?? AuditLimits.DefaultPage), ct).ConfigureAwait(false)), ct)),
            "query", "aud.change_log", AuditPermissions.ViewChanges);

        Tag(aud.MapGet("/history/{entityType}/{id:guid}", (string entityType, Guid id, IAuditQueryService s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, AuditPermissions.ViewHistory, async () =>
                await s.HistoryAsync(entityType, id, ct).ConfigureAwait(false) is { } items ? Results.Ok(items) : NotFound(), ct)),
            "record", "aud.change_log", AuditPermissions.ViewHistory);

        Tag(aud.MapGet("/access-log", (
                [FromQuery] Guid? userId, [FromQuery] string? entityType, [FromQuery] Guid? entityId, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
                [FromQuery] string? cursor, [FromQuery] int? limit, IAuditQueryService s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, AuditPermissions.ViewAccessLog, async () =>
                Results.Ok(await s.AccessLogAsync(new AccessLogQuery(userId, entityType, entityId, from, to, cursor, limit ?? AuditLimits.DefaultPage), ct).ConfigureAwait(false)), ct)),
            "query", "aud.access_log", AuditPermissions.ViewAccessLog);

        Tag(aud.MapGet("/security-events", (
                [FromQuery] string? eventType, [FromQuery] string? severity, [FromQuery] string? actor, [FromQuery] string? subject,
                [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, [FromQuery] string? cursor, [FromQuery] int? limit,
                IAuditQueryService s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, AuditPermissions.ViewSecurityEvents, async () =>
                severity is not null && !SeverityValues.Contains(severity) ? Invalid("errors.audit.filter_invalid")
                    : Results.Ok(await s.SecurityEventsAsync(new SecurityEventQuery(eventType, severity, actor, subject, from, to, cursor, limit ?? AuditLimits.DefaultPage), ct).ConfigureAwait(false)), ct)),
            "query", "aud.security_events", AuditPermissions.ViewSecurityEvents);

        Tag(aud.MapGet("/my-sign-ins", ([FromQuery] string? cursor, [FromQuery] int? limit, IAuditQueryService s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, "iam.self.manage", async () =>
                Results.Ok(await s.MySignInsAsync(cursor, limit ?? AuditLimits.DefaultPage, ct).ConfigureAwait(false)), ct)),
            "self", "aud.security_events", "iam.self.manage");

        Tag(aud.MapGet("/export", (
                [FromQuery] Guid? userId, [FromQuery] string? entityType, [FromQuery] Guid? entityId, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
                [FromQuery] string? action, [FromQuery] string? source, IAuditQueryService s, IPermissionChecker p, HttpContext http, CancellationToken ct) =>
            RunAsync(p, AuditPermissions.Export, async () =>
            {
                if (ValidChange(action, source) is { } bad)
                {
                    return bad;
                }

                var export = await s.ExportChangesAsync(new ChangeQuery(userId, entityType, entityId, from, to, action, source, null, AuditLimits.ExportRows), ct).ConfigureAwait(false);
                http.Response.Headers["X-Export-Rows"] = export.Rows.ToString(CultureInfo.InvariantCulture);
                http.Response.Headers["X-Export-Truncated"] = export.Truncated ? "true" : "false";
                return Results.File(Encoding.UTF8.GetBytes(export.Csv), "text/csv; charset=utf-8", "change-log.csv");
            }, ct)),
            "query", "aud.change_log", AuditPermissions.Export);

        Tag(aud.MapPost("/verify", (VerifyRequest body, IAuditVerifier v, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, AuditPermissions.Verify, async () =>
                body.From is null || body.To is null ? Invalid("errors.audit.range_invalid")
                    : Results.Ok(await v.VerifyAsync(body.From.Value, body.To.Value, ct).ConfigureAwait(false)), ct)),
            "verify", "aud.change_log", AuditPermissions.Verify);
        return app;
    }

    private static IResult? ValidChange(string? action, string? source) =>
        (action is not null && !Actions.Contains(action)) || (source is not null && !Sources.Contains(source)) ? Invalid("errors.audit.filter_invalid") : null;

    private static async Task<IResult> RunAsync(IPermissionChecker permissions, string permission, Func<Task<IResult>> action, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(permission, cancellationToken).ConfigureAwait(false))
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "errors.access.forbidden");
        }

        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (ArgumentException ex) when (ex.Message.StartsWith("errors.audit.", StringComparison.Ordinal))
        {
            return Invalid(ex.Message);
        }
        catch (AccessDeniedException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: ex.ErrorCode);
        }
    }

    private static void Tag(RouteHandlerBuilder endpoint, string kind, string resource, string permission) =>
        endpoint.WithMetadata(new AuditEndpointMetadata(kind, resource, permission));

    private static IResult Invalid(string error) => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: error);

    private static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "errors.audit.not_found");
}
