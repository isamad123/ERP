using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Application.Audit;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.Identity;
using Erp.Kernel.Contracts.Access;
using Erp.Kernel.Contracts.Generated;
using Erp.Kernel.Infrastructure.Audit;
using Erp.Kernel.Infrastructure.Identity;
using Erp.Kernel.Infrastructure.Settings;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Access;

/// <summary>
/// API clients (slice S1.27c Build item 10; FR-ADM-SEC-02): the keys of connected systems such as clock-machine gateways and data feeds. A key is
/// <c>&lt;client_id&gt;.&lt;secret&gt;</c>; the secret is 32 random bytes, shown once, and kept only as an Argon2id hash (the S1.05 hasher). A client has
/// scopes (permission codes flagged <c>api_client</c>, nothing else), an IP allow list, a per-minute limit and an optional expiry. Rotation keeps the old secret
/// working for <c>iam.api_client.rotation_grace_minutes</c>; revoking is immediate. Create and rotate wait for a second person while
/// <c>iam.maker_checker.sensitive_roles</c> is on: the requester is shown the new secret once, but it opens nothing until the approval (the held request
/// carries only the hash). Every action writes a change-log entry that names no secret and no hash.
/// </summary>
public sealed class ApiClientService(
    ITenantConnectionProvider tenant,
    IClock clock,
    ICurrentActor actor,
    IAuditContext audit,
    IPermissionChecker permissions,
    IPasswordHasher hasher,
    IHeldChanges held,
    ApiUsageRecorder usage) : IApiClientService
{
    public const int MaxNameLength = 100;
    public const int MaxRate = 1_000_000;
    public const int MaxScopes = 50;
    public const int MaxIpEntries = 100;

    public async Task<IReadOnlyList<ApiClientScopeOption>> ScopeOptionsAsync(CancellationToken cancellationToken)
    {
        await RequireAsync(ApiClientPermissions.Read, cancellationToken).ConfigureAwait(false);
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(c, null, "select code, module_id, description_i18n ->> 'key' from iam.permissions where is_api_client and retired_at is null order by code",
            r => new ApiClientScopeOption(r.GetString(0), r.GetString(1), r.GetString(2)), cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApiClientSecret> CreateAsync(ApiClientCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireAsync(ApiClientPermissions.Manage, cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        var plan = await ValidateAsync(c, request, now, cancellationToken).ConfigureAwait(false);
        var (clientId, secret) = ApiKeys.NewKey();
        var id = Guid.CreateVersion7(now);
        var hash = hasher.Hash(secret);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        Guid? heldId = null;
        if (await AccessDb.SettingOnAsync(c, tx, KernelSettingKeys.IamMakerCheckerSensitiveRoles, cancellationToken).ConfigureAwait(false))
        {
            var payload = new JsonObject
            {
                ["id"] = id.ToString(),
                ["clientId"] = clientId,
                ["secretHash"] = hash,
                ["name"] = plan.Name,
                ["scopes"] = new JsonArray(plan.Scopes.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
                ["ipAllow"] = new JsonArray(plan.IpAllow.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
                ["rate"] = plan.Rate,
                ["expiresAt"] = plan.ExpiresAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            };
            heldId = await held.SubmitAsync(new HeldChangeRequest(ApiClientCodes.HeldCreate, ApiClientCodes.Table, id, payload.ToJsonString(), null), tx, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ApiClientStore.InsertAsync(c, tx, id, clientId, hash, plan, actor.UserId, actor.UserId, now, audit, cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ApiClientSecret(heldId is null ? id : null, clientId, secret, clientId + "." + secret, heldId, null);
    }

    public async Task<IReadOnlyList<ApiClientView>> ListAsync(CancellationToken cancellationToken)
    {
        await RequireAsync(ApiClientPermissions.Read, cancellationToken).ConfigureAwait(false);
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(c, null, ApiClientStore.Select + " where not is_deleted order by created_at desc, id desc", r => ApiClientStore.ReadView(r, clock.UtcNow), cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApiClientView> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await RequireAsync(ApiClientPermissions.Read, cancellationToken).ConfigureAwait(false);
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        return await FindAsync(c, id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApiClientSecret> RotateAsync(Guid id, CancellationToken cancellationToken)
    {
        await RequireAsync(ApiClientPermissions.Manage, cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        var view = await FindAsync(c, id, cancellationToken).ConfigureAwait(false);
        if (view.Status != "active")
        {
            throw new ApiClientException(400, ApiClientException.NotActive);
        }

        var secret = ApiKeys.NewSecret();
        var hash = hasher.Hash(secret);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        Guid? heldId = null;
        DateTimeOffset? validUntil = null;
        if (await AccessDb.SettingOnAsync(c, tx, KernelSettingKeys.IamMakerCheckerSensitiveRoles, cancellationToken).ConfigureAwait(false))
        {
            var payload = new JsonObject { ["id"] = id.ToString(), ["secretHash"] = hash };
            heldId = await held.SubmitAsync(new HeldChangeRequest(ApiClientCodes.HeldRotate, ApiClientCodes.Table, id, payload.ToJsonString(), null), tx, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            validUntil = await ApiClientStore.RotateAsync(c, tx, id, hash, actor.UserId, now, audit, cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ApiClientSecret(id, view.ClientId, secret, view.ClientId + "." + secret, heldId, validUntil);
    }

    public async Task<ApiClientView> RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        await RequireAsync(ApiClientPermissions.Manage, cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var status = await IdentityDb.ScalarAsync<string>(c, tx, "select status from iam.api_clients where id = $1 and not is_deleted for update", cancellationToken, id).ConfigureAwait(false)
            ?? throw new ApiClientException(404, ApiClientException.NotFound);
        if (status != "active")
        {
            throw new ApiClientException(400, ApiClientException.NotActive);
        }

        await IdentityDb.ExecuteAsync(c, tx, """
            update iam.api_clients set status = 'revoked', revoked_at = $2, previous_secret_hash = null, previous_valid_until = null,
                   updated_at = $2, updated_by = $3, row_version = row_version + 1
            where id = $1
            """, cancellationToken, id, now, actor.UserId).ConfigureAwait(false);
        await ApiClientStore.LogAsync(c, tx, id, "status", [("status", "active", "revoked")], actor.UserId, now, audit, cancellationToken).ConfigureAwait(false);
        await ApiClientStore.RaiseAsync(c, tx, ApiClientEvents.Revoked, id, now, new { id }, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await FindAsync(c, id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApiClientUsage> UsageAsync(Guid id, int days, CancellationToken cancellationToken)
    {
        await RequireAsync(ApiClientPermissions.Read, cancellationToken).ConfigureAwait(false);
        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        var view = await FindAsync(c, id, cancellationToken).ConfigureAwait(false);
        await usage.FlushAsync(cancellationToken).ConfigureAwait(false); // this host's counts of the last minute count too
        var from = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime).AddDays(-(Math.Clamp(days <= 0 ? 30 : days, 1, 366) - 1));
        var rows = await ReadAsync(c, null, """
            select usage_date, calls, errors_4xx, errors_5xx, rate_limited, max_ms, latency_histogram::text
            from iam.api_client_usage where client_id = $1 and usage_date >= $2 order by usage_date desc
            """, r => new ApiClientUsageDay(DateOnly.FromDateTime(r.GetDateTime(0)), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), LatencyHistogram.Percentile95(r.GetString(6), r.GetInt32(5)), r.GetInt32(5)),
            cancellationToken, id, from.ToDateTime(TimeOnly.MinValue)).ConfigureAwait(false);
        return new ApiClientUsage(id, view.LastUsedAt, rows);
    }

    // ------------------------------------------------------------------ rules

    private async Task RequireAsync(string permission, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(permission, cancellationToken).ConfigureAwait(false))
        {
            throw new ApiClientException(403, ApiClientException.Forbidden);
        }
    }

    private async Task<ApiClientView> FindAsync(NpgsqlConnection c, Guid id, CancellationToken cancellationToken) =>
        (await ReadAsync(c, null, ApiClientStore.Select + " where id = $1 and not is_deleted", r => ApiClientStore.ReadView(r, clock.UtcNow), cancellationToken, id).ConfigureAwait(false)).SingleOrDefault()
            ?? throw new ApiClientException(404, ApiClientException.NotFound);

    /// <summary>The checks of a new client (also run again when an approver applies a held one).</summary>
    internal static async Task<ApiClientPlan> ValidateAsync(NpgsqlConnection c, ApiClientCreateRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0 || name.Length > MaxNameLength)
        {
            throw new ApiClientException(400, ApiClientException.NameRequired);
        }

        var scopes = (request.Scopes ?? []).Select(s => (s ?? string.Empty).Trim()).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (scopes.Count == 0 || scopes.Count > MaxScopes)
        {
            throw new ApiClientException(400, ApiClientException.ScopesRequired);
        }

        var allowed = (await ReadAsync(c, null, "select code from iam.permissions where is_api_client and retired_at is null and code = any($1)", r => r.GetString(0), cancellationToken, (object)scopes.ToArray()).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        if (scopes.Any(s => !allowed.Contains(s)))
        {
            throw new ApiClientException(400, ApiClientException.ScopeNotAllowed);
        }

        var ips = new List<string>();
        foreach (var entry in request.IpAllow ?? [])
        {
            var text = (entry ?? string.Empty).Trim();
            if (!IpRanges.TryNormalize(text, out var normalized) || ips.Count >= MaxIpEntries)
            {
                throw new ApiClientException(400, ApiClientException.IpInvalid);
            }

            if (!ips.Contains(normalized, StringComparer.Ordinal))
            {
                ips.Add(normalized);
            }
        }

        if (request.RateLimitPerMinute is < 1 or > MaxRate)
        {
            throw new ApiClientException(400, ApiClientException.RateInvalid);
        }

        if (request.ExpiresAt is { } expires && expires <= now)
        {
            throw new ApiClientException(400, ApiClientException.ExpiryInvalid);
        }

        return new ApiClientPlan(name, scopes, ips, request.RateLimitPerMinute, request.ExpiresAt?.ToUniversalTime());
    }

    internal static async Task<List<T>> ReadAsync<T>(NpgsqlConnection c, NpgsqlTransaction? tx, string sql, Func<NpgsqlDataReader, T> map, CancellationToken cancellationToken, params object?[] values)
    {
        await using var command = IdentityDb.Command(c, tx, sql, values);
        var list = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(map(reader));
        }

        return list;
    }
}

/// <summary>A validated, normalized new client.</summary>
internal sealed record ApiClientPlan(string Name, IReadOnlyList<string> Scopes, IReadOnlyList<string> IpAllow, int Rate, DateTimeOffset? ExpiresAt);

/// <summary>Key material: the client id and the secret (32 random bytes, base64url); the key is <c>client_id.secret</c>.</summary>
public static class ApiKeys
{
    public const string Prefix = "ak_";

    public static (string ClientId, string Secret) NewKey() => (Prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12)), NewSecret());

    public static string NewSecret() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    /// <summary>Splits <c>client_id.secret</c>; false for anything else (neither part may be empty).</summary>
    public static bool TryParse(string? key, out string clientId, out string secret)
    {
        clientId = secret = string.Empty;
        if (string.IsNullOrEmpty(key) || key.Length > 200)
        {
            return false;
        }

        var dot = key.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot == key.Length - 1)
        {
            return false;
        }

        clientId = key[..dot];
        secret = key[(dot + 1)..];
        return clientId.StartsWith(Prefix, StringComparison.Ordinal);
    }
}

/// <summary>IP allow list entries: a single address or a CIDR range; checked by address, not by text.</summary>
public static class IpRanges
{
    public static bool TryNormalize(string text, out string normalized)
    {
        normalized = string.Empty;
        if (text.Length == 0 || text.Length > 64)
        {
            return false;
        }

        if (!text.Contains('/', StringComparison.Ordinal))
        {
            if (!IPAddress.TryParse(text, out var single) || text.Contains('%', StringComparison.Ordinal))
            {
                return false;
            }

            normalized = Canonical(single).ToString();
            return true;
        }

        if (!IPNetwork.TryParse(text, out var network))
        {
            return false;
        }

        normalized = network.BaseAddress + "/" + network.PrefixLength;
        return true;
    }

    /// <summary>True when the caller's address is in an entry (an IPv4-mapped IPv6 caller counts as its IPv4 address).</summary>
    public static bool Allows(IEnumerable<string> entries, IPAddress? caller)
    {
        if (caller is null)
        {
            return false;
        }

        var address = Canonical(caller);
        foreach (var entry in entries)
        {
            if (entry.Contains('/', StringComparison.Ordinal))
            {
                if (IPNetwork.TryParse(entry, out var network) && network.Contains(address))
                {
                    return true;
                }
            }
            else if (IPAddress.TryParse(entry, out var single) && Canonical(single).Equals(address))
            {
                return true;
            }
        }

        return false;
    }

    private static IPAddress Canonical(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}

/// <summary>The store of API clients: shared by the service, the approver's handler, the key authentication and the daily job.</summary>
internal static class ApiClientStore
{
    public const string Select = """
        select id, client_id, name, scopes, ip_allow, rate_limit_per_minute, expires_at, last_used_at, status, previous_valid_until, revoked_at, created_at
        from iam.api_clients
        """;

    public static ApiClientView ReadView(NpgsqlDataReader r, DateTimeOffset now)
    {
        var expires = r.IsDBNull(6) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(6);
        var status = r.GetString(8);
        return new ApiClientView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetFieldValue<string[]>(3), r.GetFieldValue<string[]>(4), r.GetInt32(5), expires,
            r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7), status == "active" && expires is { } e && e <= now ? "expired" : status,
            r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9), r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10), r.GetFieldValue<DateTimeOffset>(11));
    }

    public static async Task InsertAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid id, string clientId, string secretHash, ApiClientPlan plan, Guid createdBy, Guid by, DateTimeOffset now, IAuditContext audit, CancellationToken ct)
    {
        await IdentityDb.ExecuteAsync(c, tx, """
            insert into iam.api_clients (id, name, client_id, secret_hash, scopes, ip_allow, rate_limit_per_minute, expires_at, status, is_deleted, created_at, created_by, updated_at, updated_by, row_version)
            values ($1, $2, $3, $4, $5, $6, $7, $8, 'active', false, $9, $10, $9, $10, 1)
            """, ct, id, plan.Name, clientId, secretHash, plan.Scopes.ToArray(), plan.IpAllow.ToArray(), plan.Rate, plan.ExpiresAt, now, createdBy).ConfigureAwait(false);
        await LogAsync(c, tx, id, "create", [("name", null, plan.Name), ("client_id", null, clientId), ("secret", null, "set"), ("scopes", null, string.Join(",", plan.Scopes)),
            ("ip_allow", null, string.Join(",", plan.IpAllow)), ("rate_limit_per_minute", null, plan.Rate), ("expires_at", null, plan.ExpiresAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture)), ("status", null, "active")], by, now, audit, ct).ConfigureAwait(false);
        await RaiseAsync(c, tx, ApiClientEvents.Created, id, now, new { id, client_id = clientId, name = plan.Name, scopes = plan.Scopes }, ct).ConfigureAwait(false);
    }

    /// <summary>Makes the new hash current and keeps the old one for the grace period; returns when the old secret stops (null when it stops now).</summary>
    public static async Task<DateTimeOffset?> RotateAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid id, string newHash, Guid by, DateTimeOffset now, IAuditContext audit, CancellationToken ct)
    {
        var status = await IdentityDb.ScalarAsync<string>(c, tx, "select status from iam.api_clients where id = $1 and not is_deleted for update", ct, id).ConfigureAwait(false);
        if (status != "active")
        {
            throw new ApiClientException(status is null ? 404 : 400, status is null ? ApiClientException.NotFound : ApiClientException.NotActive);
        }

        var grace = await GraceMinutesAsync(c, tx, ct).ConfigureAwait(false);
        DateTimeOffset? validUntil = grace > 0 ? now.AddMinutes(grace) : null;
        await IdentityDb.ExecuteAsync(c, tx, """
            update iam.api_clients set previous_secret_hash = case when $3::timestamptz is null then null else secret_hash end, previous_valid_until = $3,
                   secret_hash = $2, updated_at = $4, updated_by = $5, row_version = row_version + 1
            where id = $1
            """, ct, id, newHash, validUntil, now, by).ConfigureAwait(false);
        await LogAsync(c, tx, id, "update", [("secret", "set", "rotated"), ("previous_valid_until", null, validUntil?.ToString("O", System.Globalization.CultureInfo.InvariantCulture))], by, now, audit, ct).ConfigureAwait(false);
        await RaiseAsync(c, tx, ApiClientEvents.Rotated, id, now, new { id, previous_valid_until = validUntil }, ct).ConfigureAwait(false);
        return validUntil;
    }

    /// <summary>Minutes the previous secret keeps working (0 = stops at once); the setting's default when unreadable.</summary>
    public static async Task<int> GraceMinutesAsync(NpgsqlConnection c, NpgsqlTransaction? tx, CancellationToken ct) =>
        (await SettingsStore.ResolveAsync(c, tx, [KernelSettingKeys.IamApiClientRotationGraceMinutes], null, null, default, ct).ConfigureAwait(false)).TryGetValue(KernelSettingKeys.IamApiClientRotationGraceMinutes, out var r)
        && r.Value.ValueKind == JsonValueKind.Number && r.Value.TryGetInt32(out var minutes) && minutes >= 0 ? minutes : 60;

    public static Task<int> LogAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid id, string action, IReadOnlyList<(string Field, object? Before, object? After)> changes, Guid by, DateTimeOffset now, IAuditContext audit, CancellationToken ct)
    {
        var array = new JsonArray();
        foreach (var (field, before, after) in changes)
        {
            var node = new JsonObject { ["field"] = field };
            if (before is not null)
            {
                node["before"] = JsonSerializer.SerializeToNode(before);
            }

            node["after"] = after is null ? null : JsonSerializer.SerializeToNode(after);
            array.Add(node);
        }

        return AuditDb.AppendChangesAsync(c, tx, [new ChangeEntry(Guid.CreateVersion7(now), now, by, audit.ActorKind, ApiClientCodes.Table, id, null, action, array, null, audit.CorrelationId, audit.IpAddress, audit.Source, by)], ct);
    }

    /// <summary>An event in the outbox of the identity schema, written in the caller's transaction (no secret, no hash in the payload).</summary>
    public static Task RaiseAsync(NpgsqlConnection c, NpgsqlTransaction? tx, string eventType, Guid id, DateTimeOffset now, object payload, CancellationToken ct) =>
        IdentityDb.ExecuteAsync(c, tx, """
            insert into iam.outbox_messages (id, occurred_at, event_type, aggregate_type, aggregate_id, payload, headers, published_at)
            values ($1, $2, $3, 'iam.api_client', $4, $5, '{}'::jsonb, null)
            """, ct, Guid.CreateVersion7(now), now, eventType, id, new JsonPayload(JsonSerializer.Serialize(payload, AccessDb.JsonOptions)));

    /// <summary>The users who hold <see cref="ApiClientPermissions.Manage"/> today, own or through a group (the people told about limits and expiry).</summary>
    public static async Task<Guid[]> HoldersAsync(NpgsqlConnection c, NpgsqlTransaction? tx, DateOnly today, CancellationToken ct) =>
        (await ApiClientService.ReadAsync(c, tx, """
            with role_ids as (
                select rp.role_id from iam.role_permissions rp join iam.permissions p on p.id = rp.permission_id and p.retired_at is null where p.code = $1)
            select u.id from iam.users u where u.status = 'active' and not u.is_deleted and (
                exists (select 1 from iam.user_roles ur where ur.user_id = u.id and ur.role_id in (select role_id from role_ids) and ur.valid_from <= $2 and (ur.valid_to is null or ur.valid_to > $2))
                or exists (select 1 from iam.user_group_members gm join iam.user_group_roles gr on gr.group_id = gm.group_id
                           where gm.user_id = u.id and gr.role_id in (select role_id from role_ids) and gr.valid_from <= $2 and (gr.valid_to is null or gr.valid_to > $2)))
            order by u.id
            """, r => r.GetGuid(0), ct, ApiClientPermissions.Manage, today.ToDateTime(TimeOnly.MinValue)).ConfigureAwait(false)).ToArray();
}
