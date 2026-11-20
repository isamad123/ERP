using Microsoft.AspNetCore.Http;

namespace Erp.Kernel.Api.Identity;

/// <summary>
/// Identity API version 3 (slice S1.12a, CR-063 item 1, ruling Q3): a browser marks its sign-in requests with the header
/// <c>X-Erp-Client: web</c>. Its refresh token then travels only in an HttpOnly cookie that page scripts cannot read, and a
/// remembered browser's device token in a second one (ruling Q4). Both cookies are Secure, SameSite=Strict and sent only to
/// <c>/api/iam/auth</c>. A cookie is never accepted without the header: another site cannot send a custom header to this
/// host (no CORS is offered), so a cross-site request cannot use the cookies. Clients without the header keep the JSON
/// body form of version 2.
/// </summary>
public static class BrowserCookies
{
    public const string ClientHeader = "X-Erp-Client";
    public const string WebClient = "web";
    public const string RefreshCookie = "__Secure-erp_rt";
    public const string DeviceCookie = "__Secure-erp_td";
    public const string CookiePath = "/api/iam/auth";

    /// <summary>True when the request comes from the web application (the header is present and says <c>web</c>).</summary>
    public static bool IsBrowser(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.Request.Headers.TryGetValue(ClientHeader, out var value) && value.Count == 1 && string.Equals(value[0], WebClient, StringComparison.Ordinal);
    }

    /// <summary>The refresh token of a browser request (cookie only), else null.</summary>
    public static string? RefreshToken(HttpContext http) =>
        IsBrowser(http) && http.Request.Cookies.TryGetValue(RefreshCookie, out var token) ? token : null;

    /// <summary>The device token of a remembered browser (cookie only, browser requests only), else null.</summary>
    public static string? DeviceToken(HttpContext http) =>
        IsBrowser(http) && http.Request.Cookies.TryGetValue(DeviceCookie, out var token) ? token : null;

    /// <summary>
    /// The refresh cookie. It has no expiry date, so the browser forgets it when it closes; the server's idle and absolute
    /// timeouts still apply while it is open.
    /// </summary>
    public static void SetRefresh(HttpContext http, string token)
    {
        ArgumentNullException.ThrowIfNull(http);
        http.Response.Cookies.Append(RefreshCookie, token, Options(null));
    }

    public static void SetDevice(HttpContext http, string token, DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(http);
        http.Response.Cookies.Append(DeviceCookie, token, Options(expiresAt));
    }

    public static void ClearRefresh(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        http.Response.Cookies.Delete(RefreshCookie, Options(null));
    }

    public static void ClearDevice(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        http.Response.Cookies.Delete(DeviceCookie, Options(null));
    }

    private static CookieOptions Options(DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = CookiePath,
        Expires = expires,
        IsEssential = true,
    };
}
