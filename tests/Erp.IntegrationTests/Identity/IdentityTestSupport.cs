using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.BuildingBlocks.Domain.Time;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Domain.Identity;
using Erp.Kernel.Infrastructure.ControlPlane;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Erp.IntegrationTests.Identity;

/// <summary>A clock the tests move forward. Each test starts it at the real time, so issued access tokens stay valid.</summary>
public sealed class TestClock : IClock
{
    private DateTimeOffset _now;

    public TestClock() => Reset();

    public DateTimeOffset UtcNow => _now;

    public void Reset() => _now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>An answer of the API: status and raw body, so tests can compare answers byte for byte.</summary>
public sealed record Answer(HttpStatusCode Status, string Body)
{
    public JsonElement Json => JsonDocument.Parse(Body).RootElement;

    public string? Text(string name) => Json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>Helpers of the identity tests (slice S1.05) over the two customers of <see cref="TenancyFixture"/>.</summary>
public sealed class IdentityHarness(TenancyFixture fx, WebApplication app, TestClock clock)
{
    public const string Password = "Correct-Horse-7";

    public TenancyFixture Fixture => fx;

    public TestClock Clock => clock;

    public static async Task<IdentityHarness> StartAsync(TenancyFixture fx, CancellationToken ct)
    {
        var clock = new TestClock();
        var app = await fx.StartAppAsync(null, ct, services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
        });
        return new IdentityHarness(fx, app, clock);
    }

    public WebApplication App => app;

    public HttpClient Client(string code, string? token = null) => TenancyFixture.Client(app, TenancyFixture.HostOf(code), token);

    public async Task<Answer> PostAsync(string code, string path, object? body, string? token = null, CancellationToken ct = default)
    {
        using var client = Client(code, token);
        using var response = await client.PostAsJsonAsync(path, body ?? new { }, ct);
        return new Answer(response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    public async Task<Answer> GetAsync(string code, string path, string? token = null, CancellationToken ct = default)
    {
        using var client = Client(code, token);
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), ct);
        return new Answer(response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    public async Task<Answer> DeleteAsync(string code, string path, string? token = null, CancellationToken ct = default)
    {
        using var client = Client(code, token);
        using var response = await client.DeleteAsync(new Uri(path, UriKind.Relative), ct);
        return new Answer(response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    public Task<Answer> SignInAsync(string code, string login, string password, CancellationToken ct) =>
        PostAsync(code, "/api/iam/auth/sign-in", new { login, password }, ct: ct);

    public Task<Answer> RefreshAsync(string code, string refreshToken, CancellationToken ct) =>
        PostAsync(code, "/api/iam/auth/refresh", new { refreshToken }, ct: ct);

    /// <summary>An administrator's token (the reserved system user; the test permission checker grants everything).</summary>
    public string AdminToken(string code) => fx.Token(code);

    public async Task<NpgsqlConnection> OwnerAsync(string code, CancellationToken ct) => await fx.OpenTenantAsync(code, TenantLogins.Owner, ct);

    /// <summary>Creates an invited user and an activation token for it (as the operator CLI does for the first admin).</summary>
    public async Task<(Guid UserId, string Username, string Token)> InviteAsync(string code, CancellationToken ct)
    {
        var username = "u" + Guid.NewGuid().ToString("N")[..10];
        var id = Guid.CreateVersion7(clock.UtcNow);
        await using var owner = await OwnerAsync(code, ct);
        await ExecAsync(owner, """
            insert into iam.users (id, username, email, display_name, language, time_zone, is_system, status, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $2 || '@example.test', $2, 'en', 'Asia/Karachi', false, 'invited', $3, $4, $3, $4)
            """, ct, id, username, clock.UtcNow, BaselineSeed.SystemUserId);
        var token = await ActivationTokens.IssueAsync(owner, id, BaselineSeed.SystemUserId, clock.UtcNow, ct);
        return (id, username, token!.Token);
    }

    /// <summary>An active user with <see cref="Password"/>, signed in once; returns its tokens.</summary>
    public async Task<(Guid UserId, string Username, Answer Tokens)> ActiveUserAsync(string code, CancellationToken ct)
    {
        var (id, username, token) = await InviteAsync(code, ct);
        var answer = await PostAsync(code, "/api/iam/auth/activate", new { token, newPassword = Password }, ct: ct);
        Assert.Equal(HttpStatusCode.OK, answer.Status);
        return (id, username, answer);
    }

    public async Task<string> StatusAsync(string code, Guid userId, CancellationToken ct)
    {
        await using var owner = await OwnerAsync(code, ct);
        await using var command = new NpgsqlCommand("select status from iam.users where id = $1", owner);
        command.Parameters.Add(new NpgsqlParameter { Value = userId });
        return (string)(await command.ExecuteScalarAsync(ct))!;
    }

    public async Task<long> CountAsync(string code, string sql, CancellationToken ct, params object[] values)
    {
        await using var owner = await OwnerAsync(code, ct);
        return await TenancyFixture.CountAsync(owner, sql, ct, values);
    }

    public async Task<List<string>> OutcomesAsync(string code, string username, CancellationToken ct)
    {
        await using var owner = await OwnerAsync(code, ct);
        await using var command = new NpgsqlCommand("select outcome from iam.login_attempts where username = $1 order by occurred_at, id", owner);
        command.Parameters.Add(new NpgsqlParameter { Value = username });
        var list = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    public async Task<List<string>> EventsAsync(string code, Guid userId, CancellationToken ct)
    {
        await using var owner = await OwnerAsync(code, ct);
        await using var command = new NpgsqlCommand("select event_type from iam.outbox_messages where aggregate_id = $1 order by occurred_at, id", owner);
        command.Parameters.Add(new NpgsqlParameter { Value = userId });
        var list = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    /// <summary>
    /// The link or code of the person's latest identity e-mail of the event (slice S1.11b2), decrypted with the customer's
    /// field key exactly as the sender job does; null when there is none (or it was already sent and wiped).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>?> EmailedAsync(string code, Guid userId, string eventCode, CancellationToken ct)
    {
        await using var owner = await OwnerAsync(code, ct);
        await using var command = new NpgsqlCommand("select secret from ntf.outbound_messages where user_id = $1 and event_code = $2 order by created_at desc, xmin::text::bigint desc limit 1", owner);
        command.Parameters.Add(new NpgsqlParameter { Value = userId });
        command.Parameters.Add(new NpgsqlParameter { Value = eventCode });
        if (await command.ExecuteScalarAsync(ct) is not byte[] secret)
        {
            return null;
        }

        var key = await fx.Secrets.GetAsync(TenantFieldKeys.Reference(code), ct) ?? throw new InvalidOperationException($"no field key for {code}");
        var plain = new AesGcmFieldEncryptor(Convert.FromBase64String(key), TenantFieldKeys.KeyVersion).Decrypt(secret, Kernel.Infrastructure.Notifications.Email.EmailOutbox.SecretPurpose);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(plain);
    }

    /// <summary>The code of the person's latest e-mail of the event (sign-in or enrolment code).</summary>
    public async Task<string> EmailedCodeAsync(string code, Guid userId, string eventCode, CancellationToken ct) =>
        (await EmailedAsync(code, userId, eventCode, ct))?["code"] ?? throw new InvalidOperationException($"no {eventCode} e-mail for {userId}");

    /// <summary>Sets a customer-level (tenant layer) or user-level setting value; returns an undo action.</summary>
    public async Task<Func<Task>> SetSettingAsync(string code, string key, string json, Guid? userId, CancellationToken ct)
    {
        await using var owner = await OwnerAsync(code, ct);
        var layer = userId is null ? "tenant" : "user";
        await ExecAsync(owner, """
            insert into cfg.setting_values (id, setting_key, layer, scope_id, value, created_at, created_by, updated_at, updated_by)
            values ($1, $2, $3, $4, $5::jsonb, $6, $7, $6, $7)
            on conflict (setting_key, layer, scope_id) do update set value = excluded.value, updated_at = excluded.updated_at, row_version = cfg.setting_values.row_version + 1
            """, ct, Guid.CreateVersion7(clock.UtcNow), key, layer, (object?)userId ?? DBNull.Value, json, clock.UtcNow, BaselineSeed.SystemUserId);
        return async () =>
        {
            await using var undo = await OwnerAsync(code, CancellationToken.None);
            await ExecAsync(undo, "delete from cfg.setting_values where setting_key = $1 and layer = $2 and scope_id is not distinct from $3", CancellationToken.None, key, layer, (object?)userId ?? DBNull.Value);
        };
    }

    /// <summary>The current authenticator code of a secret returned by an enrolment.</summary>
    public string Code(string base32Secret, TimeSpan offset = default) => Totp.Code(Base32.Decode(base32Secret), Totp.StepAt(clock.UtcNow + offset));

    /// <summary>Enrols and confirms an authenticator app through self-service; returns the secret and the recovery codes.</summary>
    public async Task<(string Secret, List<string> RecoveryCodes)> EnrolAsync(string code, string accessToken, CancellationToken ct)
    {
        var start = await PostAsync(code, "/api/iam/me/mfa-devices", new { label = "phone", currentPassword = Password }, accessToken, ct);
        Assert.Equal(HttpStatusCode.OK, start.Status);
        var secret = start.Text("secret")!;
        var confirm = await PostAsync(code, $"/api/iam/me/mfa-devices/{start.Text("deviceId")}/confirm", new { code = Code(secret) }, accessToken, ct);
        Assert.Equal(HttpStatusCode.OK, confirm.Status);
        return (secret, confirm.Json.GetProperty("recoveryCodes").EnumerateArray().Select(e => e.GetString()!).ToList());
    }

    public static async Task ExecAsync(NpgsqlConnection connection, string sql, CancellationToken ct, params object?[] values)
    {
#pragma warning disable CA2100 // test SQL text
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        }

        await command.ExecuteNonQueryAsync(ct);
    }
}
