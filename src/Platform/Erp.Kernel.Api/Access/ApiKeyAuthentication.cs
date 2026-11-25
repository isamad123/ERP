using System.Diagnostics;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Contracts.Api;
using Erp.Kernel.Contracts.Access;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Erp.Kernel.Api.Access;

/// <summary>
/// The <c>ApiKey</c> authentication scheme (slice S1.27c Build item 11; FR-ADM-SEC-02): <c>Authorization: ApiKey &lt;client_id&gt;.&lt;secret&gt;</c> beside the
/// session scheme. The customer comes from the host (D-02) and the key is looked up in that customer's database only; the principal is a client, never a
/// user. A refusal answers 401 (403 for an address outside the client's allow list) without saying which check failed; the reason is a security event.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string FailureItem = "erp.apikey.failure";
    internal const string ClientItem = "erp.apikey.client";
    internal const string PrincipalItem = "erp.apikey.principal";
    private const string Prefix = ApiClientCodes.Scheme + " ";

    /// <summary>True when the request asks for key authentication (the session scheme handles everything else).</summary>
    public static bool IsKeyRequest(HttpRequest request) => request.Headers.Authorization.ToString().StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var tenant = Context.RequestServices.GetRequiredService<ITenantContext>();
        if (!tenant.IsResolved)
        {
            return AuthenticateResult.Fail("no customer");
        }

        var key = Request.Headers.Authorization.ToString()[Prefix.Length..].Trim();
        var result = await Context.RequestServices.GetRequiredService<IApiKeyAuthenticator>()
            .AuthenticateAsync(key, Context.Connection.RemoteIpAddress?.ToString(), Context.RequestAborted).ConfigureAwait(false);
        if (result.ClientRowId is { } id)
        {
            Context.Items[ClientItem] = id; // the call is metered even when it is refused
        }

        if (result.Principal is not { } principal)
        {
            Context.Items[FailureItem] = result.Failure;
            return AuthenticateResult.Fail(result.Failure.ToString());
        }

        var claims = new List<Claim>
        {
            new("sub", principal.Id.ToString()),
            new(ApiClientCodes.ActorClaim, ApiClientCodes.ActorClaimValue),
            new("tenant", tenant.TenantCode),
            new("client_id", principal.ClientId),
            new("name", principal.Name),
        };
        claims.AddRange(principal.Scopes.Select(s => new Claim(ApiClientCodes.ScopeClaim, s)));
        Context.Items[PrincipalItem] = principal;
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, ApiClientCodes.Scheme, "name", null)), ApiClientCodes.Scheme));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var failure = Context.Items[FailureItem] as ApiKeyFailure? ?? ApiKeyFailure.Malformed;
        (var status, var title) = failure switch
        {
            ApiKeyFailure.IpNotAllowed => (StatusCodes.Status403Forbidden, "errors.iam.api_key.ip_not_allowed"),
            ApiKeyFailure.Busy => (StatusCodes.Status503ServiceUnavailable, "errors.iam.api_key.busy"),
            _ => (StatusCodes.Status401Unauthorized, "errors.iam.api_key.invalid"),
        };
        Response.Headers.WWWAuthenticate = ApiClientCodes.Scheme;
        return Results.Problem(statusCode: status, title: title).ExecuteAsync(Context);
    }
}

/// <summary>The middleware that keeps an API key inside its scopes, its rate limit and the usage record (slice S1.27c Build items 11 to 13).</summary>
public static class ApiClientPipeline
{
    /// <summary>Before authentication: times the call and adds it to the client's usage, also when the key was refused after the client was found.</summary>
    public static IApplicationBuilder UseErpApiClientMetering(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(async (context, next) =>
        {
            if (!ApiKeyAuthenticationHandler.IsKeyRequest(context.Request))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var clock = Stopwatch.StartNew();
            try
            {
                await next(context).ConfigureAwait(false);
            }
            finally
            {
                if (context.Items[ApiKeyAuthenticationHandler.ClientItem] is Guid client && context.Items[RefusedItem] is not true)
                {
                    context.RequestServices.GetRequiredService<IApiClientMeter>().Record(client, context.Response.StatusCode, clock.Elapsed.TotalMilliseconds, false);
                }
            }
        });
    }

    private const string RefusedItem = "erp.apikey.refused";

    /// <summary>
    /// After authentication: a key never reaches <c>/api/iam</c>, and reaches only endpoints that name a permission among its scopes; a call over the
    /// client's limit or the customer's ceiling gets 429 with <c>Retry-After</c>; the rest runs with the client's scopes as its only rights.
    /// </summary>
    public static IApplicationBuilder UseErpApiClientGate(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(async (context, next) =>
        {
            if (context.User.FindFirstValue(ApiClientCodes.ActorClaim) != ApiClientCodes.ActorClaimValue || context.Items[ApiKeyAuthenticationHandler.PrincipalItem] is not ApiKeyPrincipal principal)
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var services = context.RequestServices;
            var meter = services.GetRequiredService<IApiClientMeter>();
            var decision = await meter.AcquireAsync(principal.Id, principal.RateLimitPerMinute, context.RequestAborted).ConfigureAwait(false);
            if (!decision.Allowed)
            {
                context.Items[RefusedItem] = true;
                meter.Record(principal.Id, StatusCodes.Status429TooManyRequests, 0, true);
                if (decision.ClientLimit)
                {
                    await meter.NoticeLimitReachedAsync(principal.Id, principal.RateLimitPerMinute, context.RequestAborted).ConfigureAwait(false);
                }

                context.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "errors.iam.api_client.rate_limited").ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            var allowed = context.GetEndpoint()?.Metadata.GetMetadata<ApiClientEndpointMetadata>() is { } meta && principal.Scopes.Contains(meta.Permission, StringComparer.Ordinal);
            if (context.Request.Path.StartsWithSegments("/api/iam", StringComparison.OrdinalIgnoreCase) || !allowed)
            {
                await Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "errors.iam.api_client.scope").ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            services.GetRequiredService<ApiClientContext>().Bind(principal.Id, principal.Scopes);
            await next(context).ConfigureAwait(false);
        });
    }
}
