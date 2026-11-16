using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Contracts.Api;
using Erp.Host.Api;
using Erp.Kernel.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Erp.IntegrationTests.Tenancy.Isolation;

/// <summary>What an endpoint does with a record, judged from its HTTP method and route (S1.04).</summary>
public enum EndpointKind
{
    Unclassified,
    List,
    Get,
    Create,
    Update,
    Delete,

    /// <summary>Anonymous sign-in step (S1.05): the customer comes from the host; A's credentials and tokens must fail on B.</summary>
    IdentityAnonymous,

    /// <summary>Acts only on the signed-in user (S1.05): B's session never sees or changes A's sessions or devices.</summary>
    IdentitySelf,

    /// <summary>Administrator action on <c>/api/iam/users/{id}/...</c> (S1.05): A's user id on B's host is 404.</summary>
    IdentityUserAction,

    /// <summary>Access-control create (S1.06): a body naming A's ids is refused on B's host and creates nothing in A.</summary>
    AccessCreate,

    /// <summary>Access-control action on a record's route (S1.06): A's id on B's host is 404.</summary>
    AccessRecordAction,

    /// <summary>Access-control query (S1.06): a query string naming A's ids is 404 or returns nothing of A's.</summary>
    AccessQuery,

    /// <summary>Access-control catalogue (S1.06): B's catalogue never shows A's roles.</summary>
    AccessCatalog,
}

/// <summary>One API endpoint as the API host maps it: method, route and the table it serves.</summary>
public sealed record ApiEndpoint(string Method, string Pattern, string? Resource, bool Anonymous, string? IdentityKind = null, string? AccessKind = null)
{
    private const string IdSegment = "/{id:guid}";

    /// <summary>Stable key, also the xUnit theory argument: "GET /api/org/companies/{id:guid}".</summary>
    public string Key => Method + " " + Pattern;

    /// <summary>The collection route without the trailing id segment.</summary>
    public string BasePath => (Pattern.EndsWith(IdSegment, StringComparison.Ordinal) ? Pattern[..^IdSegment.Length] : Pattern).TrimEnd('/');

    public EndpointKind Kind
    {
        get
        {
            switch (AccessKind)
            {
                case "create": return EndpointKind.AccessCreate;
                case "record_action": return EndpointKind.AccessRecordAction;
                case "query": return EndpointKind.AccessQuery;
                case "catalog": return EndpointKind.AccessCatalog;
                case not null: return EndpointKind.Unclassified;
            }

            switch (IdentityKind)
            {
                case "anonymous": return EndpointKind.IdentityAnonymous;
                case "self": return EndpointKind.IdentitySelf;
                case "user_action": return EndpointKind.IdentityUserAction;
                case not null: return EndpointKind.Unclassified;
            }

            var byId = Pattern.EndsWith(IdSegment, StringComparison.Ordinal) && Pattern.IndexOf('{', StringComparison.Ordinal) == Pattern.Length - IdSegment.Length + 1;
            var hasParameter = Pattern.Contains('{', StringComparison.Ordinal);
            return (Method, byId, hasParameter) switch
            {
                ("GET", false, false) => EndpointKind.List,
                ("GET", true, _) => EndpointKind.Get,
                ("POST", false, false) => EndpointKind.Create,
                ("PUT", true, _) => EndpointKind.Update,
                ("DELETE", true, _) => EndpointKind.Delete,
                _ => EndpointKind.Unclassified,
            };
        }
    }

    public override string ToString() => Key;
}

/// <summary>
/// Every endpoint of the API host, read from the endpoint metadata of the real pipeline (S1.04 build item 1). New
/// endpoints join the isolation suite automatically; one that cannot be probed fails the suite instead of being skipped.
/// </summary>
public static class EndpointInventory
{
    /// <summary>
    /// Endpoints that need no login and serve no customer data, so there is nothing to cross. Adding to this list is a
    /// security decision: it needs the product owner's approval and a note in the slice report.
    /// </summary>
    public static readonly IReadOnlySet<string> TenantFree = new HashSet<string>(StringComparer.Ordinal) { "* /health" };

    /// <summary>
    /// Sign-in steps that answer without a login (slice S1.05). They are customer endpoints: the customer comes from the
    /// host, and the suite probes each with customer A's credentials and tokens on B's host. Reviewed list: a new
    /// anonymous endpoint fails the suite until it is added here (security decision, noted in the slice report).
    /// </summary>
    public static readonly IReadOnlySet<string> AnonymousSignIn = new HashSet<string>(StringComparer.Ordinal)
    {
        "POST /api/iam/auth/sign-in",
        "POST /api/iam/auth/mfa",
        "POST /api/iam/auth/password-change",
        "POST /api/iam/auth/mfa-enrolment",
        "POST /api/iam/auth/mfa-enrolment/confirm",
        "POST /api/iam/auth/refresh",
        "POST /api/iam/auth/activate",
    };

    private static readonly Lazy<IReadOnlyList<ApiEndpoint>> Discovered = new(DiscoverFromHost);

    /// <summary>The endpoints of <see cref="ApiHost"/> as built for production (not started, no database needed).</summary>
    public static IReadOnlyList<ApiEndpoint> All => Discovered.Value;

    /// <summary>xUnit theory rows: one per endpoint that needs a customer.</summary>
    public static TheoryData<string> CustomerEndpoints()
    {
        var data = new TheoryData<string>();
        foreach (var endpoint in All.Where(e => !TenantFree.Contains(e.Key)))
        {
            data.Add(endpoint.Key);
        }

        return data;
    }

    public static ApiEndpoint Find(string key) => All.Single(e => e.Key == key);

    /// <summary>Reads the endpoints of a built host.</summary>
    public static IReadOnlyList<ApiEndpoint> Read(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var list = new List<ApiEndpoint>();
        foreach (var endpoint in ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>())
        {
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
            var access = endpoint.Metadata.GetMetadata<AccessEndpointMetadata>();
            var resource = endpoint.Metadata.GetMetadata<GeneratedEndpointMetadata>()?.Resource ?? access?.Resource;
            var identity = endpoint.Metadata.GetMetadata<IdentityEndpointMetadata>()?.Kind;
            var anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null || endpoint.Metadata.GetMetadata<IAuthorizeData>() is null;
            foreach (var method in methods is { Count: > 0 } ? methods : ["*"])
            {
                list.Add(new ApiEndpoint(method, "/" + endpoint.RoutePattern.RawText!.TrimStart('/'), resource, anonymous, identity, access?.Kind));
            }
        }

        return [.. list.OrderBy(e => e.Pattern, StringComparer.Ordinal).ThenBy(e => e.Method, StringComparer.Ordinal)];
    }

    private static IReadOnlyList<ApiEndpoint> DiscoverFromHost()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration["ConnectionStrings:ControlPlane"] = "Host=discovery.invalid;Database=unused";
        builder.Configuration["Auth:Issuer"] = "https://discovery.invalid";
        builder.Configuration["Auth:Audience"] = "discovery";
        var secrets = new SigningKeyOnly();
        TokenSigningKeys.EnsureAsync(secrets, "discovery", CancellationToken.None).GetAwaiter().GetResult();
        var app = ApiHost.Build(builder, secrets);
        return Read(app);
    }

    /// <summary>
    /// Discovery never opens a database. The host only reads its token signing key at start-up (S1.05), so this store
    /// holds a throw-away key in memory and nothing else.
    /// </summary>
    private sealed class SigningKeyOnly : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public Task<string?> GetAsync(string reference, CancellationToken cancellationToken) =>
            reference.StartsWith("platform/token-signing/", StringComparison.Ordinal)
                ? Task.FromResult(_values.GetValueOrDefault(reference))
                : throw new InvalidOperationException("not used during discovery");

        public Task PutAsync(string reference, string value, CancellationToken cancellationToken)
        {
            _values[reference] = value;
            return Task.CompletedTask;
        }
    }
}
