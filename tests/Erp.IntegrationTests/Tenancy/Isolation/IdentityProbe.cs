using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Erp.Kernel.Domain.Identity;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Npgsql;

namespace Erp.IntegrationTests.Tenancy.Isolation;

/// <summary>
/// The cross-customer probe of the identity endpoints (slice S1.05), the counterpart of <see cref="CrossTenantProbe"/>
/// for endpoints that do not serve one generated table. Customer A gets a full set of real identity material (an
/// active user with a password, a live session and refresh token, one-time tokens of every purpose, an invited user's
/// activation token, a recovery code, a pending authenticator device). Then:
/// <list type="number">
/// <item>Anonymous steps: A's material sent to B's host (with and without headers/query naming A) is 401.</item>
/// <item>Self-service: B's own session never sees A's ids and cannot revoke, confirm or remove A's sessions or devices.</item>
/// <item>Administrator actions: A's user id on B's host is 404.</item>
/// <item>Crossed host and token, missing or forged token: 401; unknown host: 404; never 5xx.</item>
/// <item>A's database is unchanged; then a positive control on A's own host shows the endpoint does work for A.</item>
/// </list>
/// </summary>
public sealed class IdentityProbe(IsolationFixture fx, WebApplication app)
{
    private const string Password = "Probe-Password-1";

    /// <summary>Customer A's product name, banner and terms (S1.15a branding): none of these may ever appear on B's host.</summary>
    private const string BrandMarker = "Probe Brand Of Customer A";

    private sealed record Material(
        Guid UserId, string Username, Guid SessionId, string Refresh, string MfaChallenge, string PasswordChange, string MfaEnrolment,
        string Activation, string RecoveryCode, Guid PendingDeviceId, string PasswordReset, Guid TrustedDeviceId, string TrustedDeviceToken, Guid SavedViewId);

    /// <summary>The list of the saved views every probe customer has one of (slice S1.12c, cfg.saved_views).</summary>
    private const string ViewList = "org.branches";

    public async Task<IReadOnlyList<string>> RunAsync(ApiEndpoint endpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var failures = new List<string>();
        var a = await MaterialAsync(fx.CodeA, ct);
        var b = await MaterialAsync(fx.CodeB, ct);
        var adminA = fx.Tenancy.Token(fx.CodeA);
        var adminB = fx.Tenancy.Token(fx.CodeB);

        // Real session tokens (with sid) through the API itself, taken before the fingerprint because sign-in writes.
        var sessionA = await SignInAsync(fx.HostA, a.Username, ct);
        var sessionB = await SignInAsync(fx.HostB, b.Username, ct);
        if (sessionA is null || sessionB is null)
        {
            failures.Add($"{endpoint}: positive control failed: a probe user could not sign in on its own host.");
            return failures;
        }

        var markers = new[] { BrandMarker, a.UserId.ToString(), a.SessionId.ToString(), a.PendingDeviceId.ToString(), sessionA.Value.SessionId.ToString(), a.Refresh, a.MfaChallenge, a.Activation, a.PasswordReset, a.TrustedDeviceId.ToString(), a.TrustedDeviceToken, a.SavedViewId.ToString() };
        var method = new HttpMethod(endpoint.Method);
        var before = await fx.FingerprintAsync(fx.CodeA, ct);

        // B's host with B's own (or no) login, aimed at A.
        var (path, body, expected, callerToken) = endpoint.Kind switch
        {
            // Forgot password answers 202 whatever the login (S1.11b2, no account enumeration); the check is that A is untouched.
            EndpointKind.IdentityAnonymous => (endpoint.Pattern, AnonymousBody(endpoint.Pattern, a),
                (HttpStatusCode?)(endpoint.Pattern switch
                {
                    "/api/iam/auth/password/forgot" => HttpStatusCode.Accepted,
                    "/api/kernel/branding" => HttpStatusCode.OK, // B's own branding, never A's (S1.15a)
                    "/api/kernel/branding/logo" => HttpStatusCode.NotFound, // B has no logo
                    _ => HttpStatusCode.Unauthorized,
                }), (string?)null),
            EndpointKind.IdentitySelf => SelfTarget(endpoint.Pattern, a, sessionB.Value.Access),
            _ => (endpoint.Pattern.Replace("{id:guid}", a.UserId.ToString(), StringComparison.Ordinal), "{\"reason\":\"probe\",\"status\":\"disabled\"}", (HttpStatusCode?)HttpStatusCode.NotFound, adminB),
        };
        foreach (var hints in new[] { false, true })
        {
            var label = hints ? "B host + headers/query naming A" : "B host";
            var response = await SendAsync(fx.HostB, callerToken, method, path, body, hints, ct);
            if ((int)response.Status >= 500)
            {
                failures.Add($"{endpoint} [{label}]: server error {(int)response.Status}. {Short(response.Body)}");
            }
            else if (expected is { } status && response.Status != status)
            {
                failures.Add($"{endpoint} [{label}]: expected {(int)status}, got {(int)response.Status}. {Short(response.Body)}");
            }
            else if (expected is null && response.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                failures.Add($"{endpoint} [{label}]: B was refused on its own host ({(int)response.Status}); the path was not exercised. {Short(response.Body)}");
            }

            Leaks(failures, endpoint, label, response.Body, markers);
        }

        // Identity API version 3 (S1.12a): A's refresh and device cookies, sent as a browser to B's host, are refused too.
        if (endpoint.Pattern is "/api/iam/auth/refresh" or "/api/iam/auth/sign-in")
        {
            var cookieBody = endpoint.Pattern.EndsWith("/sign-in", StringComparison.Ordinal) ? Json(new { login = a.Username, password = Password }) : "{}";
            var response = await SendAsync(fx.HostB, null, method, path, cookieBody, hints: false, ct,
                cookies: $"__Secure-erp_rt={a.Refresh}; __Secure-erp_td={a.TrustedDeviceToken}");
            if (response.Status != HttpStatusCode.Unauthorized)
            {
                failures.Add($"{endpoint} [B host + A's browser cookies]: expected 401, got {(int)response.Status}. {Short(response.Body)}");
            }

            Leaks(failures, endpoint, "B host + A's browser cookies", response.Body, markers);
        }

        // Crossed hosts and tokens (endpoints that need a login).
        if (endpoint.Kind != EndpointKind.IdentityAnonymous)
        {
            using var forger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var tokenA = endpoint.Kind == EndpointKind.IdentitySelf ? sessionA.Value.Access : adminA;
            var tokenB = endpoint.Kind == EndpointKind.IdentitySelf ? sessionB.Value.Access : adminB;
            var crossed = new (string Label, string Host, string? Token, HttpStatusCode Expected)[]
            {
                ("A host + B token", fx.HostA, tokenB, HttpStatusCode.Unauthorized),
                ("B host + A token", fx.HostB, tokenA, HttpStatusCode.Unauthorized),
                ("A host + no token", fx.HostA, null, HttpStatusCode.Unauthorized),
                ("A host + token signed by an untrusted key", fx.HostA, fx.Tenancy.Token(fx.CodeA, key: forger), HttpStatusCode.Unauthorized),
                ("unknown host + A token", "unknown-" + fx.Tenancy.Suffix + ".erp.test", tokenA, HttpStatusCode.NotFound),
            };
            foreach (var (label, host, token, status) in crossed)
            {
                var response = await SendAsync(host, token, method, path, body, hints: false, ct);
                if (response.Status != status)
                {
                    failures.Add($"{endpoint} [{label}]: expected {(int)status}, got {(int)response.Status}. {Short(response.Body)}");
                }

                Leaks(failures, endpoint, label, response.Body, markers);
            }
        }
        else
        {
            var response = await SendAsync("unknown-" + fx.Tenancy.Suffix + ".erp.test", null, method, path, body, hints: false, ct);
            if (response.Status != HttpStatusCode.NotFound)
            {
                failures.Add($"{endpoint} [unknown host]: expected 404, got {(int)response.Status}.");
            }
        }

        var changed = IsolationFixture.Changed(before, await fx.FingerprintAsync(fx.CodeA, ct));
        if (changed.Count > 0)
        {
            failures.Add($"{endpoint}: customer A's database changed during the cross-customer calls: {string.Join(", ", changed)}");
        }

        // Positive control: the same endpoint works for A on A's host.
        var own = await PositiveControlAsync(endpoint, a, sessionA.Value, adminA, ct);
        if (own is not null)
        {
            failures.Add($"{endpoint}: positive control failed: {own}");
        }

        return failures;
    }

    private static string? AnonymousBody(string pattern, Material a) => pattern switch
    {
        "/api/iam/auth/sign-in" => Json(new { login = a.Username, password = Password }),
        "/api/iam/auth/mfa" => Json(new { challengeToken = a.MfaChallenge, recoveryCode = a.RecoveryCode }),
        "/api/iam/auth/password-change" => Json(new { challengeToken = a.PasswordChange, newPassword = "Probe-New-Pass-22" }),
        "/api/iam/auth/mfa-enrolment" => Json(new { challengeToken = a.MfaEnrolment }),
        "/api/iam/auth/mfa-enrolment/confirm" => Json(new { challengeToken = a.MfaEnrolment, deviceId = a.PendingDeviceId, code = "123456" }),
        "/api/iam/auth/refresh" => Json(new { refreshToken = a.Refresh }),
        "/api/iam/auth/activate" => Json(new { token = a.Activation, newPassword = "Probe-New-Pass-22" }),
        "/api/iam/auth/password/forgot" => Json(new { login = a.Username }),
        "/api/iam/auth/password/reset" => Json(new { token = a.PasswordReset, newPassword = "Probe-New-Pass-22" }),
        "/api/iam/auth/mfa/email-code" => Json(new { challengeToken = a.MfaChallenge }),
        "/api/kernel/branding" or "/api/kernel/branding/logo" => null,
        _ => throw new InvalidOperationException($"No probe body for anonymous endpoint {pattern}; extend IdentityProbe."),
    };

    /// <summary>Path, body and expected status of a self-service call by B that names A's ids where the route has one (null = B's own success).</summary>
    private static (string Path, string? Body, HttpStatusCode? Expected, string? Token) SelfTarget(string pattern, Material a, string tokenB) => pattern switch
    {
        "/api/iam/me/sessions/{id:guid}" => (pattern.Replace("{id:guid}", a.SessionId.ToString(), StringComparison.Ordinal), null, HttpStatusCode.NotFound, tokenB),
        "/api/iam/me/mfa-devices/{id:guid}/remove" => (pattern.Replace("{id:guid}", a.PendingDeviceId.ToString(), StringComparison.Ordinal), Json(new { currentPassword = Password }), HttpStatusCode.NotFound, tokenB),
        "/api/iam/me/mfa-devices/{id:guid}/confirm" => (pattern.Replace("{id:guid}", a.PendingDeviceId.ToString(), StringComparison.Ordinal), Json(new { code = "123456" }), HttpStatusCode.BadRequest, tokenB),
        "/api/iam/me/password" => (pattern, Json(new { currentPassword = Password, newPassword = "Probe-New-Pass-22" }), null, tokenB),
        "/api/iam/me/trusted-devices/{id:guid}" => (pattern.Replace("{id:guid}", a.TrustedDeviceId.ToString(), StringComparison.Ordinal), null, HttpStatusCode.NotFound, tokenB),
        // Saved views (S1.12c): B cannot change or delete A's view (404), and B's own list and new view never show A's.
        "/api/cfg/saved-views/{id:guid}" => (pattern.Replace("{id:guid}", a.SavedViewId.ToString(), StringComparison.Ordinal) + "?rowVersion=1", ViewBody(), HttpStatusCode.NotFound, tokenB),
        "/api/cfg/saved-views/" => (pattern + "?list=" + ViewList, ViewBody(), null, tokenB),
        _ when !pattern.Contains('{', StringComparison.Ordinal) => (pattern, Json(new { currentPassword = Password, label = "probe" }), null, tokenB),
        _ => throw new InvalidOperationException($"No probe target for self-service endpoint {pattern}; extend IdentityProbe."),
    };

    private async Task<string?> PositiveControlAsync(ApiEndpoint endpoint, Material a, (string Access, Guid SessionId) session, string adminA, CancellationToken ct)
    {
        var method = new HttpMethod(endpoint.Method);
        Reply reply;
        switch (endpoint.Kind)
        {
            case EndpointKind.IdentityAnonymous when endpoint.Pattern == "/api/iam/auth/mfa-enrolment/confirm":
                var start = await SendAsync(fx.HostA, null, HttpMethod.Post, "/api/iam/auth/mfa-enrolment", Json(new { challengeToken = a.MfaEnrolment }), false, ct);
                using (var doc = JsonDocument.Parse(start.Body))
                {
                    var secret = Base32.Decode(doc.RootElement.GetProperty("secret").GetString()!);
                    var code = Totp.Code(secret, Totp.StepAt(DateTimeOffset.UtcNow));
                    var emailCode = await EmailedCodeAsync(fx.CodeA, a.UserId, "identity.enrolment_code", ct); // R-003 (S1.11b2)
                    reply = await SendAsync(fx.HostA, null, method, endpoint.Pattern, Json(new { challengeToken = a.MfaEnrolment, deviceId = doc.RootElement.GetProperty("deviceId").GetGuid(), code, emailCode }), false, ct);
                }

                return reply.Status == HttpStatusCode.OK ? null : $"A's own enrolment was not confirmed ({(int)reply.Status}). {Short(reply.Body)}";
            case EndpointKind.IdentityAnonymous:
                if (endpoint.Pattern == "/api/iam/auth/mfa/email-code")
                {
                    // The e-mail factor (S1.11b2): A allows it and A's probe user has one.
                    await using var admin = await fx.OpenAdminAsync(fx.CodeA, ct);
                    await ExecAsync(admin, """
                        insert into cfg.setting_values (id, setting_key, layer, scope_id, value, created_at, created_by, updated_at, updated_by)
                        values ($1, 'iam.mfa.allowed_methods', 'tenant', null, '["totp","email"]'::jsonb, now(), $2, now(), $2)
                        on conflict (setting_key, layer, scope_id) do update set value = excluded.value
                        """, ct, Guid.CreateVersion7(), BaselineSeed.SystemUserId);
                    await ExecAsync(admin, """
                        insert into iam.mfa_devices (id, user_id, kind, label, status, created_at, created_by, updated_at, updated_by)
                        values ($1, $2, 'email', 'probe', 'active', now(), $2, now(), $2)
                        """, ct, Guid.CreateVersion7(), a.UserId);
                }

                reply = await SendAsync(fx.HostA, null, method, endpoint.Pattern, AnonymousBody(endpoint.Pattern, a), false, ct);
                if (endpoint.Pattern == "/api/kernel/branding/logo")
                {
                    return reply.Status == HttpStatusCode.NotFound ? null : $"A has no logo, so 404 was expected on A's host, got {(int)reply.Status}.";
                }

                if (endpoint.Pattern == "/api/kernel/branding")
                {
                    return reply.Status == HttpStatusCode.OK && reply.Body.Contains(BrandMarker, StringComparison.Ordinal) ? null : $"A's own branding was not served on A's host ({(int)reply.Status}). {Short(reply.Body)}";
                }

                return (int)reply.Status is >= 200 and < 300 ? null : $"A's own material was refused on A's host ({(int)reply.Status}). {Short(reply.Body)}";
            case EndpointKind.IdentitySelf:
                var path = endpoint.Pattern
                    .Replace("/saved-views/{id:guid}", "/saved-views/" + a.SavedViewId + "?rowVersion=1", StringComparison.Ordinal)
                    .Replace("/sessions/{id:guid}", "/sessions/" + a.SessionId, StringComparison.Ordinal)
                    .Replace("/trusted-devices/{id:guid}", "/trusted-devices/" + a.TrustedDeviceId, StringComparison.Ordinal)
                    .Replace("{id:guid}", a.PendingDeviceId.ToString(), StringComparison.Ordinal);
                if (endpoint.Pattern == "/api/cfg/saved-views/")
                {
                    path += "?list=" + ViewList;
                }

                string? body = endpoint.Pattern switch
                {
                    "/api/cfg/saved-views/" or "/api/cfg/saved-views/{id:guid}" => method == HttpMethod.Delete ? null : ViewBody(),
                    "/api/iam/me/password" => Json(new { currentPassword = Password, newPassword = "Probe-Own-Pass-33" }),
                    "/api/iam/me/mfa-devices/{id:guid}/confirm" => Json(new { code = "123456" }),
                    _ when method == HttpMethod.Get || method == HttpMethod.Delete => null,
                    _ => Json(new { currentPassword = Password, label = "probe" }),
                };
                reply = await SendAsync(fx.HostA, session.Access, method, path, body, false, ct);
                if (endpoint.Pattern == "/api/iam/me/sessions" && !reply.Body.Contains(session.SessionId.ToString(), StringComparison.Ordinal))
                {
                    return $"A's own session list does not contain A's session. {Short(reply.Body)}";
                }

                if (endpoint.Key == "GET /api/cfg/saved-views/" && !reply.Body.Contains(a.SavedViewId.ToString(), StringComparison.Ordinal))
                {
                    return $"A's own saved views do not contain A's view. {Short(reply.Body)}";
                }

                // The confirm call is found but refused for a wrong code (400); every other call is served.
                var served = endpoint.Pattern.EndsWith("/confirm", StringComparison.Ordinal) ? reply.Status == HttpStatusCode.BadRequest : (int)reply.Status is >= 200 and < 300 || (endpoint.Pattern == "/api/iam/me/recovery-codes" && reply.Status == HttpStatusCode.BadRequest);
                return served ? null : $"A was not served on its own host ({(int)reply.Status}). {Short(reply.Body)}";
            default:
                reply = await SendAsync(fx.HostA, adminA, method, endpoint.Pattern.Replace("{id:guid}", a.UserId.ToString(), StringComparison.Ordinal), "{\"reason\":\"probe\",\"status\":\"disabled\"}", false, ct);
                return reply.Status is HttpStatusCode.OK or HttpStatusCode.NoContent or HttpStatusCode.BadRequest
                    ? null
                    : $"A's own user was not found or refused on A's host ({(int)reply.Status}). {Short(reply.Body)}";
        }
    }

    /// <summary>Writes a fresh, complete set of identity material into the customer's database (as the database owner would).</summary>
    private async Task<Material> MaterialAsync(string code, CancellationToken ct)
    {
        using var hasher = new Argon2idPasswordHasher();
        var actor = BaselineSeed.SystemUserId;
        var user = Guid.CreateVersion7();
        var invited = Guid.CreateVersion7();
        var session = Guid.CreateVersion7();
        var device = Guid.CreateVersion7();
        var username = "probe" + Guid.NewGuid().ToString("N")[..12];
        var refresh = session.ToString("N") + "." + OpaqueTokens.New();
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mfa_challenge"] = OpaqueTokens.New(),
            ["password_change"] = OpaqueTokens.New(),
            ["mfa_enrolment"] = OpaqueTokens.New(),
            ["activation"] = OpaqueTokens.New(),
            ["password_reset"] = OpaqueTokens.New(),
        };
        var recovery = OpaqueTokens.NewRecoveryCode();
        var trusted = Guid.CreateVersion7();
        var trustedToken = OpaqueTokens.New();

        await using var c = await fx.OpenAdminAsync(code, ct);
        await ExecAsync(c, """
            insert into iam.users (id, username, email, display_name, language, time_zone, is_system, status, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $2 || '@probe.test', $2, 'en', 'Asia/Karachi', false, 'active', now(), $3, now(), $3),
                   ($4, $2 || 'i', $2 || 'i@probe.test', $2, 'en', 'Asia/Karachi', false, 'invited', now(), $3, now(), $3)
            """, ct, user, username, actor, invited);
        var hash = hasher.Hash(Password);
        await ExecAsync(c, "insert into iam.user_credentials (user_id, password_hash, hash_algorithm, password_changed_at, must_change, failed_attempts) values ($1, $2, $3, now(), false, 0)", ct, user, hash, hasher.Algorithm);
        await ExecAsync(c, "insert into iam.password_history (id, user_id, password_hash, created_at, created_by) values ($1, $2, $3, now(), $2)", ct, Guid.CreateVersion7(), user, hash);
        await ExecAsync(c, """
            insert into iam.sessions (id, user_id, issued_at, last_seen_at, expires_at, refresh_token_hash, created_at, created_by)
            values ($1, $2, now(), now(), now() + interval '12 hours', $3, now(), $2)
            """, ct, session, user, OpaqueTokens.Hash(refresh));
        foreach (var (purpose, token) in tokens)
        {
            await ExecAsync(c, """
                insert into iam.one_time_tokens (id, user_id, purpose, token_hash, expires_at, created_at, created_by, updated_at, updated_by)
                values ($1, $2, $3, $4, now() + interval '1 hour', now(), $5, now(), $5)
                """, ct, Guid.CreateVersion7(), purpose == "activation" ? invited : user, purpose, OpaqueTokens.Hash(token), actor);
        }

        await ExecAsync(c, "insert into iam.mfa_recovery_codes (id, user_id, code_hash, created_at, created_by, updated_at, updated_by) values ($1, $2, $3, now(), $2, now(), $2)", ct, Guid.CreateVersion7(), user, hasher.Hash(recovery));
        await ExecAsync(c, """
            insert into iam.mfa_devices (id, user_id, kind, secret, label, status, created_at, created_by, updated_at, updated_by)
            values ($1, $2, 'totp', $3, 'probe', 'pending', now(), $2, now(), $2)
            """, ct, device, user, await EncryptedSecretAsync(code, ct));
        // A remembered browser (slice S1.12a, iam.trusted_devices).
        await ExecAsync(c, """
            insert into iam.trusted_devices (id, user_id, token_hash, label, expires_at, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, 'Chrome', now() + interval '30 days', now(), $2, now(), $2)
            """, ct, trusted, user, OpaqueTokens.Hash(trustedToken));
        // A saved list view (slice S1.12c, cfg.saved_views).
        var view = Guid.CreateVersion7();
        await ExecAsync(c, """
            insert into cfg.saved_views (id, user_id, list_key, name, definition, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, 'probe view', '{"search": "probe"}'::jsonb, now(), $2, now(), $2)
            """, ct, view, user, ViewList);
        // Login branding (slice S1.15a): every probe customer has its own product name, banner and terms.
        foreach (var (key, text) in new[] { ("cfg.branding.product_name", BrandMarker), ("cfg.branding.banner_en", BrandMarker + " banner"), ("cfg.branding.terms_ur", BrandMarker + " terms") })
        {
            await ExecAsync(c, """
                insert into cfg.setting_values (id, setting_key, layer, scope_id, value, created_at, created_by, updated_at, updated_by)
                values ($1, $2, 'tenant', null, to_jsonb($3::text), now(), $4, now(), $4)
                on conflict (setting_key, layer, scope_id) do update set value = excluded.value
                """, ct, Guid.CreateVersion7(), key, code == fx.CodeA ? text : "Neighbour brand of B", actor);
        }

        return new Material(user, username, session, refresh, tokens["mfa_challenge"], tokens["password_change"], tokens["mfa_enrolment"], tokens["activation"], recovery, device, tokens["password_reset"], trusted, trustedToken, view);
    }

    /// <summary>The code of the latest identity e-mail of the event, decrypted with the customer's field key as the sender does (S1.11b2).</summary>
    private async Task<string> EmailedCodeAsync(string code, Guid userId, string eventCode, CancellationToken ct)
    {
        await using var c = await fx.OpenAdminAsync(code, ct);
        await using var command = new NpgsqlCommand("select secret from ntf.outbound_messages where user_id = $1 and event_code = $2 order by created_at desc, xmin::text::bigint desc limit 1", c);
        command.Parameters.Add(new NpgsqlParameter { Value = userId });
        command.Parameters.Add(new NpgsqlParameter { Value = eventCode });
        var secret = (byte[])(await command.ExecuteScalarAsync(ct))!;
        var key = await fx.Tenancy.Secrets.GetAsync(TenantFieldKeys.Reference(code), ct) ?? throw new InvalidOperationException($"no field key for {code}");
        var plain = new AesGcmFieldEncryptor(Convert.FromBase64String(key), TenantFieldKeys.KeyVersion).Decrypt(secret, Kernel.Infrastructure.Notifications.Email.EmailOutbox.SecretPurpose);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(plain)!["code"];
    }

    /// <summary>An authenticator secret encrypted with the customer's own field key, as enrolment stores it.</summary>
    private async Task<byte[]> EncryptedSecretAsync(string code, CancellationToken ct)
    {
        var key = await fx.Tenancy.Secrets.GetAsync(TenantFieldKeys.Reference(code), ct) ?? throw new InvalidOperationException($"no field key for {code}");
        return new AesGcmFieldEncryptor(Convert.FromBase64String(key), TenantFieldKeys.KeyVersion).Encrypt(RandomNumberGenerator.GetBytes(Totp.SecretBytes), "iam.mfa_devices.secret");
    }

    private async Task<(string Access, Guid SessionId)?> SignInAsync(string host, string username, CancellationToken ct)
    {
        var reply = await SendAsync(host, null, HttpMethod.Post, "/api/iam/auth/sign-in", Json(new { login = username, password = Password }), false, ct);
        if (reply.Status != HttpStatusCode.OK)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(reply.Body);
        return (doc.RootElement.GetProperty("accessToken").GetString()!, doc.RootElement.GetProperty("sessionId").GetGuid());
    }

    private static void Leaks(List<string> failures, ApiEndpoint endpoint, string label, string body, IEnumerable<string> markers)
    {
        var found = markers.Where(m => body.Contains(m, StringComparison.OrdinalIgnoreCase)).ToList();
        if (found.Count > 0)
        {
            failures.Add($"{endpoint} [{label}]: LEAK: the response contains customer A's identity data {string.Join(", ", found)}. {Short(body)}");
        }
    }

    private async Task<Reply> SendAsync(string host, string? token, HttpMethod method, string pathAndQuery, string? body, bool hints, CancellationToken ct, string? cookies = null)
    {
        using var client = TenancyFixture.Client(app, host, token);
        var target = hints ? pathAndQuery + (pathAndQuery.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "tenant=" + fx.CodeA + "&tenantCode=" + fx.CodeA : pathAndQuery;
        using var request = new HttpRequestMessage(method, target);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        }

        if (cookies is not null)
        {
            request.Headers.Add("X-Erp-Client", "web"); // a browser request (Identity API version 3)
            request.Headers.Add("Cookie", cookies);
        }

        if (hints)
        {
            request.Headers.Add("X-Tenant", fx.CodeA);
            request.Headers.Add("X-Tenant-Code", fx.CodeA);
            request.Headers.Add("X-Forwarded-Host", fx.HostA);
            request.Headers.Add("Forwarded", "host=" + fx.HostA);
        }

        using var response = await client.SendAsync(request, ct);
        return new Reply(response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    private static async Task ExecAsync(NpgsqlConnection connection, string sql, CancellationToken ct, params object[] values)
    {
#pragma warning disable CA2100 // test SQL text
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    private static string Json(object value) => JsonSerializer.Serialize(value, JsonSerializerOptions.Web);

    /// <summary>A saved view to create or change (S1.12c): the list key is read on create only, the row version on change only.</summary>
    private static string ViewBody() => Json(new { listKey = ViewList, name = "probe " + Guid.NewGuid().ToString("N")[..8], definition = new { search = "x" }, rowVersion = 1 });

    private static string Short(string body) => body.Length <= 300 ? body : body[..300] + "…";

    private sealed record Reply(HttpStatusCode Status, string Body);
}
