using System.Security.Claims;
using Erp.BuildingBlocks.Application.Audit;
using Erp.Kernel.Contracts.Access;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Erp.Kernel.Api.Audit;

/// <summary>
/// One correlation id per request (slice S1.07 build item 1): created by the server, never taken from the request, and
/// returned in the <c>X-Correlation-Id</c> answer header. The client address and the source are fixed here too: a person
/// signed in through the web is <c>ui</c> (ruling Q8; API clients arrive later as <c>api</c>), anything else <c>system</c>.
/// </summary>
public static class AuditCorrelation
{
    public const string Header = "X-Correlation-Id";
    private const string ItemKey = "erp.audit.request";

    public static IApplicationBuilder UseErpAuditCorrelation(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(async (context, next) =>
        {
            var info = new AuditRequestInfo(Guid.CreateVersion7(), context.Connection.RemoteIpAddress?.ToString(), AuditSources.Ui);
            context.Items[ItemKey] = info;
            context.Response.Headers[Header] = info.CorrelationId.ToString();
            var previous = AuditAmbient.Current;
            AuditAmbient.Current = info;
            try
            {
                await next(context).ConfigureAwait(false);
            }
            finally
            {
                AuditAmbient.Current = previous;
            }
        });
    }

    public static AuditRequestInfo? Of(HttpContext? context) => context?.Items[ItemKey] as AuditRequestInfo;
}

/// <summary>The audit context of an API request: the signed-in user (from the token's <c>sub</c>) and the request info.</summary>
public sealed class HttpAuditContext(IHttpContextAccessor accessor) : IAuditContext
{
    private readonly AuditRequestInfo _fallback = new(Guid.CreateVersion7(), null, AuditSources.System);

    private AuditRequestInfo Info => AuditCorrelation.Of(accessor.HttpContext) ?? _fallback;

    public Guid CorrelationId => Info.CorrelationId;

    public string? IpAddress => Info.IpAddress;

    private bool IsApiClient => accessor.HttpContext?.User.FindFirstValue(ApiClientCodes.ActorClaim) == ApiClientCodes.ActorClaimValue;

    public string Source => ActorUserId is null ? AuditSources.System : IsApiClient ? AuditSources.Api : Info.Source;

    public Guid? ActorUserId =>
        Guid.TryParse(accessor.HttpContext?.User.FindFirstValue("sub") ?? accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public string ActorKind => ActorUserId is null ? ActorKinds.System : IsApiClient ? ActorKinds.ApiClient : ActorKinds.User;
}
