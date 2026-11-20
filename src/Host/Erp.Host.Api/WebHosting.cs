using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace Erp.Host.Api;

/// <summary>
/// The web application served by the API host (slice S1.12a, Build item 8, ruling Q2): same address as the API, so the
/// customer is found from the host name and the sign-in cookies stay on it. Setting <c>Web:Root</c> names the folder of
/// the production build (<c>web/apps/web/dist</c>); without it the host serves the API only. A GET or HEAD for any other
/// path that is not <c>/api</c> answers <c>index.html</c>, so deep links work. Every answer, API ones included, carries the
/// security headers: a content security policy that allows only the host's own scripts, styles, fonts and connections, no
/// framing, no referrer, no content sniffing (spec 13 FR-UX-PRF-02: nothing is fetched from another host).
/// </summary>
public static class WebHosting
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; connect-src 'self'; " +
        "object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    /// <summary>The build folder from <c>Web:Root</c> when it holds an <c>index.html</c>, else null (API only).</summary>
    public static string? Root(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var root = config["Web:Root"];
        return root is { Length: > 0 } && File.Exists(Path.Combine(root, "index.html")) ? Path.GetFullPath(root) : null;
    }

    /// <summary>True for a request the web application answers: GET or HEAD outside <c>/api</c> and <c>/health</c>.</summary>
    public static bool IsWebRequest(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = context.Request.Path;
        return (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
            && !path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Security headers on every answer; call first in the pipeline.</summary>
    public static IApplicationBuilder UseErpSecurityHeaders(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers["Content-Security-Policy"] = ContentSecurityPolicy;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["X-Frame-Options"] = "DENY";
                headers["Referrer-Policy"] = "no-referrer";
                headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
                headers["Cross-Origin-Opener-Policy"] = "same-origin";
                if (context.Request.IsHttps)
                {
                    headers["Strict-Transport-Security"] = "max-age=31536000";
                }

                return Task.CompletedTask;
            });
            return next(context);
        });

    /// <summary>
    /// Serves the build's files and answers <c>index.html</c> for other web paths. Call after tenant resolution (an unknown
    /// host has already been answered 404) and before authentication; it only acts on requests no endpoint matched.
    /// </summary>
    public static IApplicationBuilder UseErpWebApplication(this IApplicationBuilder app, string root)
    {
        ArgumentNullException.ThrowIfNull(app);
        var files = new PhysicalFileProvider(root);
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            ContentTypeProvider = new FileExtensionContentTypeProvider(),
            OnPrepareResponse = c =>
            {
                // Built files carry a content hash in their name, so they never change; the page itself is always checked.
                c.Context.Response.Headers[HeaderNames.CacheControl] = c.Context.Request.Path.StartsWithSegments("/assets", StringComparison.Ordinal)
                    ? "public, max-age=31536000, immutable"
                    : "no-cache";
            },
        });
        var index = files.GetFileInfo("index.html");
        return app.Use(async (context, next) =>
        {
            if (context.GetEndpoint() is not null || !IsWebRequest(context) || Path.HasExtension(context.Request.Path.Value))
            {
                await next(context).ConfigureAwait(false); // a missing file with an extension stays 404, not the page
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers[HeaderNames.CacheControl] = "no-cache";
            if (HttpMethods.IsHead(context.Request.Method))
            {
                context.Response.ContentLength = index.Length;
                return;
            }

            await context.Response.SendFileAsync(index, context.RequestAborted).ConfigureAwait(false);
        });
    }
}
