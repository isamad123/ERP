using System.Net;
using Erp.Host.Api;
using Erp.IntegrationTests.Tenancy;
using Microsoft.AspNetCore.Builder;

namespace Erp.IntegrationTests.Web;

/// <summary>
/// The API host serves the web application (slice S1.12a, Build item 8, ruling Q2; spec 13 FR-UX-PRF-02, FR-UX-SEC-07):
/// its files and, for any other non-API path, <c>index.html</c>, so deep links work; API paths that do not exist still answer
/// 404; an unknown host gets nothing; a locked customer still gets the application (it shows the locked page itself); every
/// answer carries the security headers. The build folder is a small stand-in written by the test.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class WebHostingTests(TenancyFixture fx) : IAsyncLifetime
{
    private const string Page = "<!doctype html><html><head><script type=\"module\" src=\"/assets/app-abc123.js\"></script></head><body><div id=\"root\"></div></body></html>";

    private WebApplication app = null!;
    private string root = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? provisionedFor;

    private string A => "w" + fx.Suffix;

    public async ValueTask InitializeAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            if (provisionedFor != fx.Suffix)
            {
                await fx.ProvisionAsync(A, Ct);
                provisionedFor = fx.Suffix;
            }
        }
        finally
        {
            Gate.Release();
        }

        root = Directory.CreateTempSubdirectory("erp-web-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "assets"));
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), Page, Ct);
        await File.WriteAllTextAsync(Path.Combine(root, "assets", "app-abc123.js"), "console.log(1);", Ct);
        await File.WriteAllTextAsync(Path.Combine(root, "boot.js"), "document.documentElement.lang='en';", Ct);
        app = await fx.StartAppAsync(null, Ct, settings: new Dictionary<string, string?> { ["Web:Root"] = root });
    }

    public async ValueTask DisposeAsync()
    {
        await app.DisposeAsync();
        Directory.Delete(root, recursive: true);
    }

    private async Task<HttpResponseMessage> GetAsync(string host, string path)
    {
        using var client = TenancyFixture.Client(app, host);
        return await client.GetAsync(new Uri(path, UriKind.Relative), Ct);
    }

    private static void HasSecurityHeaders(HttpResponseMessage response)
    {
        var csp = string.Join(" ", response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
    }

    [Fact]
    public async Task S112a_the_application_answers_its_own_paths_and_deep_links_with_strict_security_headers()
    {
        var host = TenancyFixture.HostOf(A);
        using var page = await GetAsync(host, "/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Page, await page.Content.ReadAsStringAsync(Ct));
        HasSecurityHeaders(page);
        Assert.Contains("no-cache", page.Headers.CacheControl!.ToString(), StringComparison.Ordinal);

        using var deep = await GetAsync(host, "/reset-password?token=abc");
        Assert.Equal(HttpStatusCode.OK, deep.StatusCode);
        Assert.Equal(Page, await deep.Content.ReadAsStringAsync(Ct));

        using var asset = await GetAsync(host, "/assets/app-abc123.js");
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Contains("immutable", asset.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        HasSecurityHeaders(asset);

        using var missing = await GetAsync(host, "/assets/missing-file.js");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode); // a missing file is not answered with the page
    }

    [Fact]
    public async Task S112a_api_paths_are_never_answered_by_the_application_and_keep_their_answers()
    {
        var host = TenancyFixture.HostOf(A);
        using var unknownApi = await GetAsync(host, "/api/no/such/thing");
        Assert.Equal(HttpStatusCode.NotFound, unknownApi.StatusCode);
        Assert.Empty(await unknownApi.Content.ReadAsStringAsync(Ct));
        HasSecurityHeaders(unknownApi);

        using var health = await GetAsync(host, "/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        using var anonymous = await GetAsync(host, "/api/kernel/capabilities");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task S112a_an_unknown_host_gets_nothing_and_a_locked_customer_still_gets_the_application()
    {
        using var unknown = await GetAsync("nobody-" + fx.Suffix + ".example.test", "/");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.DoesNotContain("<html", await unknown.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        await fx.SetDaysAfterEndAsync(A, 60, Ct); // Locked
        try
        {
            using var page = await GetAsync(TenancyFixture.HostOf(A), "/settings/licence");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Equal(Page, await page.Content.ReadAsStringAsync(Ct));
        }
        finally
        {
            await fx.SetDaysAfterEndAsync(A, null, Ct);
        }
    }

    [Fact]
    public async Task S112a_without_a_web_root_the_host_serves_the_api_only()
    {
        Assert.Null(WebHosting.Root(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()));
        var apiOnly = await fx.StartAppAsync(null, Ct);
        try
        {
            using var client = TenancyFixture.Client(apiOnly, TenancyFixture.HostOf(A));
            using var page = await client.GetAsync(new Uri("/", UriKind.Relative), Ct);
            Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
            HasSecurityHeaders(page);
        }
        finally
        {
            await apiOnly.DisposeAsync();
        }
    }
}
