using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Contracts.Api;
using Erp.Host.Api;
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
}

/// <summary>One API endpoint as the API host maps it: method, route and the table it serves.</summary>
public sealed record ApiEndpoint(string Method, string Pattern, string? Resource, bool Anonymous)
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
            var resource = endpoint.Metadata.GetMetadata<GeneratedEndpointMetadata>()?.Resource;
            var anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null || endpoint.Metadata.GetMetadata<IAuthorizeData>() is null;
            foreach (var method in methods is { Count: > 0 } ? methods : ["*"])
            {
                list.Add(new ApiEndpoint(method, "/" + endpoint.RoutePattern.RawText!.TrimStart('/'), resource, anonymous));
            }
        }

        return [.. list.OrderBy(e => e.Pattern, StringComparer.Ordinal).ThenBy(e => e.Method, StringComparer.Ordinal)];
    }

    private static IReadOnlyList<ApiEndpoint> DiscoverFromHost()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration["ConnectionStrings:ControlPlane"] = "Host=discovery.invalid;Database=unused";
        var app = ApiHost.Build(builder, new NoSecrets());
        return Read(app);
    }

    /// <summary>Discovery never opens a database, so no secret is ever read.</summary>
    private sealed class NoSecrets : ISecretStore
    {
        public Task<string?> GetAsync(string reference, CancellationToken cancellationToken) => throw new InvalidOperationException("not used during discovery");

        public Task PutAsync(string reference, string value, CancellationToken cancellationToken) => throw new InvalidOperationException("not used during discovery");
    }
}
