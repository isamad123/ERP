using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Application.Audit;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.Identity;
using Erp.Kernel.Contracts.Access;
using Erp.Kernel.Contracts.Generated;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Contracts.Settings;
using Erp.Kernel.Infrastructure.Audit;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Access;

/// <summary>
/// Remembers that a presented key matched a stored hash, so the slow Argon2id check runs once per key and host instead of on every call. The entry holds
/// the SHA-256 of the whole key and the stored hash it matched; the row is read on every call, so a rotation, a revoke or an expiry takes effect at once
/// (a changed hash never matches the remembered one). Nothing but hashes is kept.
/// </summary>
public sealed class ApiKeyVerificationCache
{
    private const int MaxEntries = 5000;
    private readonly ConcurrentDictionary<string, string> _matched = new(StringComparer.Ordinal);

    public bool Matches(string key, string storedHash) => _matched.TryGetValue(Digest(key), out var hash) && string.Equals(hash, storedHash, StringComparison.Ordinal);

    public void Remember(string key, string storedHash)
    {
        if (_matched.Count >= MaxEntries)
        {
            _matched.Clear();
        }

        _matched[Digest(key)] = storedHash;
    }

    private static string Digest(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
}

/// <summary>Limits how often one kind of refusal from one address is written as a security event, so a flood of bad keys cannot flood the audit tables.</summary>
public sealed class ApiKeyRefusalThrottle(IClock clock)
{
    private const int MaxEntries = 10_000;
    private readonly ConcurrentDictionary<string, long> _last = new(StringComparer.Ordinal);

    public bool ShouldRecord(string kind)
    {
        var minute = clock.UtcNow.ToUnixTimeSeconds() / 60;
        if (_last.Count >= MaxEntries)
        {
            _last.Clear();
        }

        var previous = _last.GetOrAdd(kind, -1);
        if (previous == minute)
        {
            return false;
        }

        _last[kind] = minute;
        return true;
    }
}

/// <summary>
/// Key authentication (slice S1.27c Build item 11; FR-ADM-SEC-02): <c>Authorization: ApiKey &lt;client_id&gt;.&lt;secret&gt;</c>. The customer is the one
/// bound from the host name, and the key is looked up in that customer's database only, so a key of another customer finds nothing. Checks in order: the
/// client exists, is active and not expired, the caller's address is in its allow list (an empty list refuses every call), then the secret against the
/// current hash or, during the grace period after a rotation, the previous one (Argon2id compares in constant time). Every refusal is a security event
/// (throttled per kind and address). <c>last_used_at</c> is written at most once a minute.
/// </summary>
public sealed class ApiKeyAuthenticator(ITenantConnectionProvider tenant, IClock clock, IPasswordHasher hasher, ApiKeyVerificationCache cache, ApiKeyRefusalThrottle throttle) : IApiKeyAuthenticator
{
    public async Task<ApiKeyResult> AuthenticateAsync(string key, string? callerAddress, CancellationToken cancellationToken)
    {
        if (!ApiKeys.TryParse(key, out var clientId, out var secret))
        {
            return new ApiKeyResult(null, ApiKeyFailure.Malformed, null);
        }

        var now = clock.UtcNow;
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        var row = (await ApiClientService.ReadAsync(c, null, """
            select id, name, secret_hash, previous_secret_hash, previous_valid_until, scopes, ip_allow, rate_limit_per_minute, expires_at, status, last_used_at
            from iam.api_clients where client_id = $1 and not is_deleted
            """, r => new Row(r.GetGuid(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4),
                r.GetFieldValue<string[]>(5), r.GetFieldValue<string[]>(6), r.GetInt32(7), r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8), r.GetString(9),
                r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10)), cancellationToken, clientId).ConfigureAwait(false)).SingleOrDefault();
        if (row is null)
        {
            await RefuseAsync(c, ApiKeyFailure.Unknown, clientId, null, callerAddress, now, cancellationToken).ConfigureAwait(false);
            return new ApiKeyResult(null, ApiKeyFailure.Unknown, null);
        }

        if (row.Status == "revoked")
        {
            await RefuseAsync(c, ApiKeyFailure.Revoked, clientId, row.Id, callerAddress, now, cancellationToken).ConfigureAwait(false);
            return new ApiKeyResult(null, ApiKeyFailure.Revoked, row.Id);
        }

        if (row.Status == "expired" || (row.ExpiresAt is { } expires && expires <= now))
        {
            await RefuseAsync(c, ApiKeyFailure.Expired, clientId, row.Id, callerAddress, now, cancellationToken).ConfigureAwait(false);
            return new ApiKeyResult(null, ApiKeyFailure.Expired, row.Id);
        }

        if (!System.Net.IPAddress.TryParse(callerAddress, out var caller) || !IpRanges.Allows(row.IpAllow, caller))
        {
            await RefuseAsync(c, ApiKeyFailure.IpNotAllowed, clientId, row.Id, callerAddress, now, cancellationToken).ConfigureAwait(false);
            return new ApiKeyResult(null, ApiKeyFailure.IpNotAllowed, row.Id);
        }

        var candidates = new List<string> { row.SecretHash };
        if (row.PreviousHash is not null && row.PreviousValidUntil is { } until && until > now)
        {
            candidates.Add(row.PreviousHash);
        }

        var matched = false;
        try
        {
            foreach (var hash in candidates)
            {
                if (cache.Matches(key, hash))
                {
                    matched = true;
                    break;
                }

                if (hasher.Verify(secret, hash, hasher.Algorithm, out _))
                {
                    cache.Remember(key, hash);
                    matched = true;
                    break;
                }
            }
        }
        catch (PasswordHasherBusyException)
        {
            return new ApiKeyResult(null, ApiKeyFailure.Busy, row.Id);
        }

        if (!matched)
        {
            await RefuseAsync(c, ApiKeyFailure.SecretInvalid, clientId, row.Id, callerAddress, now, cancellationToken).ConfigureAwait(false);
            return new ApiKeyResult(null, ApiKeyFailure.SecretInvalid, row.Id);
        }

        var scopes = await ApiClientService.ReadAsync(c, null, "select code from iam.permissions where is_api_client and retired_at is null and code = any($1) order by code", r => r.GetString(0), cancellationToken, (object)row.Scopes).ConfigureAwait(false);
        if (row.LastUsedAt is null || row.LastUsedAt <= now.AddMinutes(-1))
        {
            await IdentityDb.ExecuteAsync(c, null, "update iam.api_clients set last_used_at = $2 where id = $1 and (last_used_at is null or last_used_at <= $3)", cancellationToken, row.Id, now, now.AddMinutes(-1)).ConfigureAwait(false);
        }

        return new ApiKeyResult(new ApiKeyPrincipal(row.Id, clientId, row.Name, scopes, row.Rate), ApiKeyFailure.None, row.Id);
    }

    private async Task RefuseAsync(NpgsqlConnection c, ApiKeyFailure failure, string clientId, Guid? rowId, string? address, DateTimeOffset now, CancellationToken ct)
    {
        var kind = failure.ToString() + "|" + (rowId?.ToString() ?? "-") + "|" + (address ?? "-");
        if (!throttle.ShouldRecord(kind))
        {
            return;
        }

        var details = new JsonObject
        {
            ["reason"] = failure.ToString(),
            ["client_id"] = clientId.Length > 40 ? clientId[..40] : clientId,
            ["ip"] = address,
        };
        try
        {
            await AuditDb.AppendSecurityEventAsync(c, null, new SecurityEventRecord(now, "iam.api_key_refused", null, rowId?.ToString(), failure is ApiKeyFailure.SecretInvalid or ApiKeyFailure.IpNotAllowed ? Severities.Warning : Severities.Info,
                details, BaselineSeed.SystemUserId), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is NpgsqlException or DbException)
        {
            // the refusal stands even when the event cannot be written
        }
    }

    private sealed record Row(Guid Id, string Name, string SecretHash, string? PreviousHash, DateTimeOffset? PreviousValidUntil, string[] Scopes, string[] IpAllow, int Rate, DateTimeOffset? ExpiresAt, string Status, DateTimeOffset? LastUsedAt);
}

/// <summary>Rate limits and usage for one request scope (the recorder is the host's).</summary>
public sealed class ApiClientMeter(ITenantContext tenantContext, ITenantConnectionProvider tenant, ISettings settings, ApiUsageRecorder recorder, IClock clock) : IApiClientMeter
{
    public const int DefaultTenantCeiling = 600;

    public async Task<ApiRateDecision> AcquireAsync(Guid clientId, int clientLimit, CancellationToken cancellationToken)
    {
        var ceiling = (await settings.GetAsync(KernelSettingKeys.ItgApiTenantRatePerMinute, (Guid?)null, cancellationToken).ConfigureAwait(false)).Value is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var n) && n > 0 ? n : DefaultTenantCeiling;
        return recorder.TryAcquire(tenantContext.TenantCode, tenant.DataSource, clientId, clientLimit, ceiling);
    }

    public void Record(Guid clientId, int status, double elapsedMilliseconds, bool refusedByLimit) =>
        recorder.Record(tenantContext.TenantCode, tenant.DataSource, clientId, status, elapsedMilliseconds, refusedByLimit);

    public async Task NoticeLimitReachedAsync(Guid clientId, int limit, CancellationToken cancellationToken)
    {
        if (!recorder.FirstRefusalToday(tenantContext.TenantCode, clientId))
        {
            return;
        }

        var now = clock.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var name = await IdentityDb.ScalarAsync<string>(c, tx, """
            update iam.api_clients set limit_notified_on = $2 where id = $1 and (limit_notified_on is null or limit_notified_on < $2) returning name
            """, cancellationToken, clientId, today.ToDateTime(TimeOnly.MinValue)).ConfigureAwait(false);
        if (name is not null)
        {
            var holders = await ApiClientStore.HoldersAsync(c, tx, today, cancellationToken).ConfigureAwait(false);
            await ApiClientStore.RaiseAsync(c, tx, ApiClientEvents.LimitReached, clientId, now, new { recipients = holders, client_name = name, limit }, cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task FlushAsync(CancellationToken cancellationToken) => recorder.FlushAsync(cancellationToken);
}

/// <summary>
/// Approval of a held creation or rotation of an API client (FR-KRN-IAM-14; S1.27c Build item 10). The request holds the new hash, never the secret: the
/// requester saw the secret once and it opens nothing until a second person approves here. The approver needs <c>iam.maker_checker.approve</c> and
/// <see cref="ApiClientPermissions.Manage"/>; the kernel refuses the requester (FR-XM-32).
/// </summary>
public abstract class ApiClientHeldHandler(IServiceProvider services, string kind) : IHeldChangeHandler
{
    public string Kind => kind;

    public string ApprovePermission => AccessControlPermissions.ApproveChanges;

    public Task<bool> VisibleAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken) =>
        services.GetRequiredService<IPermissionChecker>().HasPermissionAsync(ApiClientPermissions.Manage, cancellationToken).AsTask();

    public async Task<HeldChangeProblem?> CheckAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        var tx = (NpgsqlTransaction)transaction;
        var now = services.GetRequiredService<IClock>().UtcNow;
        try
        {
            if (kind == ApiClientCodes.HeldCreate)
            {
                await ApiClientService.ValidateAsync(tx.Connection!, ReadCreate(change), now, cancellationToken).ConfigureAwait(false);
                var clientId = (string?)JsonNode.Parse(change.Payload)?["clientId"] ?? string.Empty;
                return await IdentityDb.ScalarAsync<bool>(tx.Connection!, tx, "select exists (select 1 from iam.api_clients where client_id = $1)", cancellationToken, clientId).ConfigureAwait(false)
                    ? new HeldChangeProblem(ApiClientException.Changed) : null;
            }

            var status = await IdentityDb.ScalarAsync<string>(tx.Connection!, tx, "select status from iam.api_clients where id = $1 and not is_deleted", cancellationToken, RotateId(change)).ConfigureAwait(false);
            return status == "active" ? null : new HeldChangeProblem(status is null ? ApiClientException.NotFound : ApiClientException.NotActive);
        }
        catch (ApiClientException e)
        {
            return new HeldChangeProblem(e.ErrorKey);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
        {
            return new HeldChangeProblem(ApiClientException.Changed);
        }
    }

    public async Task<HeldChangeProblem?> ApplyAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (await CheckAsync(change, transaction, cancellationToken).ConfigureAwait(false) is { } problem)
        {
            return problem;
        }

        var tx = (NpgsqlTransaction)transaction;
        var c = tx.Connection!;
        var now = services.GetRequiredService<IClock>().UtcNow;
        var by = services.GetRequiredService<ICurrentActor>().UserId;
        var audit = services.GetRequiredService<IAuditContext>();
        var node = JsonNode.Parse(change.Payload)!;
        if (kind == ApiClientCodes.HeldCreate)
        {
            var plan = await ApiClientService.ValidateAsync(c, ReadCreate(change), now, cancellationToken).ConfigureAwait(false);
            await ApiClientStore.InsertAsync(c, tx, Guid.Parse((string)node["id"]!), (string)node["clientId"]!, (string)node["secretHash"]!, plan, change.RequestedBy, by, now, audit, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ApiClientStore.RotateAsync(c, tx, RotateId(change), (string)node["secretHash"]!, by, now, audit, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    public async Task<HeldChangeDescription> DescribeAsync(HeldChange change, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        var tx = (NpgsqlTransaction)transaction;
        var details = new List<HeldChangeDetail>();
        string name;
        if (kind == ApiClientCodes.HeldCreate)
        {
            var request = ReadCreate(change);
            name = request.Name;
            details.Add(new HeldChangeDetail("iam.api_client.held.scopes", "items", Items: request.Scopes.ToList()));
            details.Add(new HeldChangeDetail("iam.api_client.held.ip_allow", "items", Items: request.IpAllow.ToList()));
            details.Add(new HeldChangeDetail("iam.api_client.held.rate", "text", Text: request.RateLimitPerMinute.ToString(CultureInfo.InvariantCulture)));
            if (request.ExpiresAt is { } expires)
            {
                details.Add(new HeldChangeDetail("iam.api_client.held.expires", "date", Date: DateOnly.FromDateTime(expires.UtcDateTime)));
            }
        }
        else
        {
            name = await IdentityDb.ScalarAsync<string>(tx.Connection!, tx, "select name from iam.api_clients where id = $1", cancellationToken, RotateId(change)).ConfigureAwait(false) ?? string.Empty;
            details.Add(new HeldChangeDetail("iam.api_client.held.rotate", "key"));
        }

        return new HeldChangeDescription(new Dictionary<string, string> { ["en"] = name, ["ur"] = name }, details);
    }

    private static Guid RotateId(HeldChange change) => Guid.Parse((string)JsonNode.Parse(change.Payload)!["id"]!);

    private static ApiClientCreateRequest ReadCreate(HeldChange change)
    {
        var node = JsonNode.Parse(change.Payload)!;
        return new ApiClientCreateRequest((string)node["name"]!, node["scopes"]!.AsArray().Select(n => (string)n!).ToList(), node["ipAllow"]!.AsArray().Select(n => (string)n!).ToList(),
            (int)node["rate"]!, node["expiresAt"] is { } e ? DateTimeOffset.Parse((string)e!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null);
    }
}

/// <summary>The approval of a held creation (kind <see cref="ApiClientCodes.HeldCreate"/>).</summary>
public sealed class ApiClientCreateHandler(IServiceProvider services) : ApiClientHeldHandler(services, ApiClientCodes.HeldCreate);

/// <summary>The approval of a held rotation (kind <see cref="ApiClientCodes.HeldRotate"/>).</summary>
public sealed class ApiClientRotateHandler(IServiceProvider services) : ApiClientHeldHandler(services, ApiClientCodes.HeldRotate);

/// <summary>
/// The daily job of API clients (<see cref="ApiClientCodes.ExpiryJob"/>, slice S1.27c Build items 10 and 13): marks the clients whose expiry has passed, tells
/// the holders of <see cref="ApiClientPermissions.Manage"/> once when a key is within <c>iam.api_client.expiry_notice_days</c> of its end, and removes rate
/// windows older than a day. Expiry is also checked on every call, so a day's delay in marking never lets an expired key in.
/// </summary>
public sealed class ApiClientExpiryJob : IJobHandler
{
    public string Code => ApiClientCodes.ExpiryJob;

    public async Task RunAsync(IJobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var sp = job.Services;
        var clock = sp.GetRequiredService<IClock>();
        var audit = sp.GetService<IAuditContext>() ?? new SystemAuditContext();
        var by = BaselineSeed.SystemUserId; // a maintenance job acts as the system, whoever asked for the run
        var now = clock.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        await using var c = await IdentityDb.OpenAsync(sp.GetRequiredService<ITenantConnectionProvider>().DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var expired = await ApiClientService.ReadAsync(c, tx, """
            update iam.api_clients set status = 'expired', previous_secret_hash = null, previous_valid_until = null, updated_at = $1, updated_by = $2, row_version = row_version + 1
            where status = 'active' and not is_deleted and expires_at is not null and expires_at <= $1 returning id
            """, r => r.GetGuid(0), cancellationToken, now, by).ConfigureAwait(false);
        foreach (var id in expired)
        {
            await ApiClientStore.LogAsync(c, tx, id, "status", [("status", "active", "expired")], by, now, audit, cancellationToken).ConfigureAwait(false);
            await ApiClientStore.RaiseAsync(c, tx, ApiClientEvents.Expired, id, now, new { id }, cancellationToken).ConfigureAwait(false);
        }

        var noticeDays = (await SettingsRead.IntAsync(c, tx, KernelSettingKeys.IamApiClientExpiryNoticeDays, 14, cancellationToken).ConfigureAwait(false));
        var soon = await ApiClientService.ReadAsync(c, tx, """
            update iam.api_clients set expiring_notified_at = $1
            where status = 'active' and not is_deleted and expires_at is not null and expires_at > $1 and expires_at <= $2 and expiring_notified_at is null
            returning id, name, expires_at
            """, r => (Id: r.GetGuid(0), Name: r.GetString(1), Expires: r.GetFieldValue<DateTimeOffset>(2)), cancellationToken, now, now.AddDays(noticeDays)).ConfigureAwait(false);
        if (soon.Count > 0)
        {
            var holders = await ApiClientStore.HoldersAsync(c, tx, today, cancellationToken).ConfigureAwait(false);
            foreach (var (id, name, expires) in soon)
            {
                var days = (int)Math.Ceiling((expires - now).TotalDays);
                await ApiClientStore.RaiseAsync(c, tx, ApiClientEvents.Expiring, id, now, new { recipients = holders, client_name = name, expires_on = expires.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), days }, cancellationToken).ConfigureAwait(false);
            }
        }

        var pruned = await IdentityDb.ExecuteAsync(c, tx, "delete from iam.api_client_windows where window_start < $1", cancellationToken, now.AddDays(-1)).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        await job.LogAsync("info", $"{expired.Count} client(s) marked expired, {soon.Count} expiry notice(s), {pruned} old rate window(s) removed.", cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Reads a whole-number setting of the whole customer inside a transaction (jobs have no signed-in person).</summary>
internal static class SettingsRead
{
    public static async Task<int> IntAsync(NpgsqlConnection c, NpgsqlTransaction tx, string key, int fallback, CancellationToken ct) =>
        (await Settings.SettingsStore.ResolveAsync(c, tx, [key], null, null, default, ct).ConfigureAwait(false)).TryGetValue(key, out var r)
        && r.Value.ValueKind == JsonValueKind.Number && r.Value.TryGetInt32(out var n) && n > 0 ? n : fallback;
}

/// <summary>Registers the API client services (hosts that serve the API and the worker that runs the daily job).</summary>
public static class ApiClientRegistration
{
    public static IServiceCollection AddKernelApiClients(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ApiUsageRecorder>();
        services.TryAddSingleton<ApiKeyVerificationCache>();
        services.TryAddSingleton<ApiKeyRefusalThrottle>();
        services.TryAddScoped<IApiClientService, ApiClientService>();
        services.TryAddScoped<IApiKeyAuthenticator, ApiKeyAuthenticator>();
        services.TryAddScoped<IApiClientMeter, ApiClientMeter>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IHeldChangeHandler, ApiClientCreateHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IHeldChangeHandler, ApiClientRotateHandler>());
        return services;
    }
}
