using System.Data.Common;
using System.Globalization;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Contracts.Access;
using Erp.Kernel.Infrastructure.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Access;

/// <summary>
/// Rate limits and usage of API clients (slice S1.27c Build items 12 and 13; FR-KRN-INT-07, FR-ADM-SEC-02). One per API host.
/// <para>
/// Rate: a fixed window per client per minute against the client's own limit and the customer's ceiling (<c>itg.api.tenant_rate_per_minute</c>, all clients
/// together). The host counts in memory; every minute (and on demand) it adds its counts to <c>iam.api_client_windows</c> and reads back what the other
/// hosts added, so a limit holds across hosts. Between two reconciles a host does not see the others' calls of the same minute, so a burst spread over
/// several hosts can exceed a limit by up to one host's share (risk R-208, accepted for 1.0).
/// </para>
/// <para>
/// Usage: calls, 4xx and 5xx answers, calls refused by a limit, the slowest answer and a histogram of answer times per client and UTC day, added to
/// <c>iam.api_client_usage</c> at the same reconcile. The 95th percentile is read from the histogram (the upper bound of its bucket).
/// </para>
/// </summary>
public sealed class ApiUsageRecorder(IClock clock)
{
    /// <summary>Upper bounds in milliseconds of the answer-time buckets; the last one is open.</summary>
    public static readonly int[] Bounds = [5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000, int.MaxValue];

    private readonly object _gate = new();
    private readonly Dictionary<string, TenantState> _tenants = new(StringComparer.Ordinal);

    /// <summary>Counts one call against the client's and the customer's limits for the current minute; a refused call is not counted in the windows.</summary>
    public ApiRateDecision TryAcquire(string tenantCode, DbDataSource source, Guid clientId, int clientLimit, int tenantLimit)
    {
        var now = clock.UtcNow;
        var window = Minute(now);
        lock (_gate)
        {
            var tenant = Tenant(tenantCode, source, window);
            var client = Client(tenant, clientId, window);
            var retryAfter = Math.Max(1, 60 - now.Second);
            if (client.Local + client.Remote >= clientLimit)
            {
                return new ApiRateDecision(false, retryAfter, true);
            }

            if (tenant.Clients.Values.Where(c => c.Window == window).Sum(c => c.Local) + tenant.Remote >= tenantLimit)
            {
                return new ApiRateDecision(false, retryAfter, false);
            }

            client.Local++;
            return new ApiRateDecision(true, 0, true);
        }
    }

    /// <summary>True once per client and day on this host: the first refusal of the day raises the limit notice (the database decides across hosts).</summary>
    public bool FirstRefusalToday(string tenantCode, Guid clientId)
    {
        var day = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        lock (_gate)
        {
            if (!_tenants.TryGetValue(tenantCode, out var tenant) || !tenant.Clients.TryGetValue(clientId, out var client) || client.NoticeDay == day)
            {
                return false;
            }

            client.NoticeDay = day;
            return true;
        }
    }

    /// <summary>Adds one answered call to the client's usage of today.</summary>
    public void Record(string tenantCode, DbDataSource source, Guid clientId, int status, double elapsedMilliseconds, bool refusedByLimit)
    {
        var now = clock.UtcNow;
        lock (_gate)
        {
            var tenant = Tenant(tenantCode, source, Minute(now));
            var client = Client(tenant, clientId, Minute(now));
            var day = DateOnly.FromDateTime(now.UtcDateTime);
            if (!client.Days.TryGetValue(day, out var delta))
            {
                client.Days[day] = delta = new DayDelta();
            }

            var ms = (int)Math.Min(int.MaxValue - 1, Math.Max(0, Math.Round(elapsedMilliseconds)));
            delta.Calls++;
            delta.Errors4xx += status is >= 400 and < 500 ? 1 : 0;
            delta.Errors5xx += status >= 500 ? 1 : 0;
            delta.RateLimited += refusedByLimit ? 1 : 0;
            delta.MaxMs = Math.Max(delta.MaxMs, ms);
            delta.Histogram[Array.FindIndex(Bounds, b => ms <= b)]++;
        }
    }

    /// <summary>
    /// Adds this host's counts to the database and reads back the other hosts' counts of the current minute. A customer whose database cannot be
    /// reached keeps its counts for the next try.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        List<TenantState> tenants;
        lock (_gate)
        {
            tenants = [.. _tenants.Values];
        }

        foreach (var tenant in tenants)
        {
            try
            {
                await FlushTenantAsync(tenant, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is NpgsqlException or DbException or InvalidOperationException)
            {
                // kept for the next flush; a counter must never take the API down
            }
        }
    }

    private async Task FlushTenantAsync(TenantState tenant, CancellationToken ct)
    {
        var now = clock.UtcNow;
        List<(Guid Client, long Window, int Calls)> windows = [];
        List<(Guid Client, DateOnly Day, DayDelta Delta)> days = [];
        long current = Minute(now);
        lock (_gate)
        {
            foreach (var client in tenant.Clients.Values)
            {
                if (client.Local > client.Flushed)
                {
                    windows.Add((client.Id, client.Window, client.Local - client.Flushed));
                }

                windows.AddRange(client.Pending.Select(p => (client.Id, p.Window, p.Calls)));
                foreach (var (day, delta) in client.Days)
                {
                    days.Add((client.Id, day, delta.Copy()));
                }
            }
        }

        if (windows.Count == 0 && days.Count == 0 && tenant.Window != current)
        {
            return;
        }

        await using var c = await IdentityDb.OpenAsync(tenant.Source, ct).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        var systemUser = ControlPlane.Provisioning.BaselineSeed.SystemUserId;
        foreach (var (client, window, calls) in windows)
        {
            await IdentityDb.ExecuteAsync(c, tx, """
                insert into iam.api_client_windows (id, client_id, window_start, calls, created_at, created_by, updated_at, updated_by, row_version)
                values ($1, $2, $3, $4, $5, $6, $5, $6, 1)
                on conflict (client_id, window_start) do update set calls = iam.api_client_windows.calls + excluded.calls, updated_at = excluded.updated_at, row_version = iam.api_client_windows.row_version + 1
                """, ct, Guid.CreateVersion7(now), client, FromMinute(window), calls, now, systemUser).ConfigureAwait(false);
        }

        foreach (var (client, day, delta) in days)
        {
            await MergeDayAsync(c, tx, client, day, delta, now, systemUser, ct).ConfigureAwait(false);
        }

        var current_ = FromMinute(current);
        var totals = await ApiClientService.ReadAsync(c, tx, "select client_id, calls from iam.api_client_windows where window_start = $1", r => (Client: r.GetGuid(0), Calls: r.GetInt32(1)), ct, current_).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);

        lock (_gate)
        {
            foreach (var (client, window, calls) in windows)
            {
                if (tenant.Clients.TryGetValue(client, out var state))
                {
                    if (state.Window == window && state.Pending.All(p => p.Window != window))
                    {
                        state.Flushed += calls;
                    }
                    else if (state.Pending.FindIndex(p => p.Window == window) is >= 0 and var at)
                    {
                        state.Pending.RemoveAt(at);
                    }
                }
            }

            foreach (var (client, day, delta) in days)
            {
                if (tenant.Clients.TryGetValue(client, out var state) && state.Days.TryGetValue(day, out var held))
                {
                    held.Subtract(delta);
                    if (held.IsEmpty)
                    {
                        state.Days.Remove(day);
                    }
                }
            }

            if (tenant.Window == current)
            {
                var flushedTotal = 0;
                foreach (var state in tenant.Clients.Values.Where(s => s.Window == current))
                {
                    var db = totals.Where(t => t.Client == state.Id).Sum(t => t.Calls);
                    state.Remote = Math.Max(0, db - state.Flushed);
                    flushedTotal += state.Flushed;
                }

                tenant.Remote = Math.Max(0, totals.Sum(t => t.Calls) - flushedTotal);
            }
        }
    }

    private static async Task MergeDayAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid client, DateOnly day, DayDelta delta, DateTimeOffset now, Guid by, CancellationToken ct)
    {
        var date = day.ToDateTime(TimeOnly.MinValue);
        var existing = (await ApiClientService.ReadAsync(c, tx, "select id, latency_histogram::text from iam.api_client_usage where client_id = $1 and usage_date = $2 for update", r => (Id: r.GetGuid(0), Json: r.GetString(1)), ct, client, date).ConfigureAwait(false)).SingleOrDefault();
        var histogram = LatencyHistogram.Merge(existing.Json, delta.Histogram);
        if (existing.Json is null)
        {
            await IdentityDb.ExecuteAsync(c, tx, """
                insert into iam.api_client_usage (id, client_id, usage_date, calls, errors_4xx, errors_5xx, rate_limited, max_ms, latency_histogram, created_at, created_by, updated_at, updated_by, row_version)
                values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $10, $11, 1)
                """, ct, Guid.CreateVersion7(now), client, date, delta.Calls, delta.Errors4xx, delta.Errors5xx, delta.RateLimited, delta.MaxMs, new JsonPayload(histogram), now, by).ConfigureAwait(false);
            return;
        }

        await IdentityDb.ExecuteAsync(c, tx, """
            update iam.api_client_usage set calls = calls + $2, errors_4xx = errors_4xx + $3, errors_5xx = errors_5xx + $4, rate_limited = rate_limited + $5, max_ms = greatest(max_ms, $6),
                   latency_histogram = $7, updated_at = $8, updated_by = $9, row_version = row_version + 1
            where id = $1
            """, ct, existing.Id, delta.Calls, delta.Errors4xx, delta.Errors5xx, delta.RateLimited, delta.MaxMs, new JsonPayload(histogram), now, by).ConfigureAwait(false);
    }

    private static long Minute(DateTimeOffset now) => now.ToUnixTimeSeconds() / 60;

    private static DateTimeOffset FromMinute(long minute) => DateTimeOffset.FromUnixTimeSeconds(minute * 60);

    private TenantState Tenant(string code, DbDataSource source, long window)
    {
        if (!_tenants.TryGetValue(code, out var tenant))
        {
            _tenants[code] = tenant = new TenantState(code, source) { Window = window };
        }

        if (tenant.Window != window)
        {
            tenant.Window = window;
            tenant.Remote = 0;
        }

        tenant.Source = source;
        return tenant;
    }

    private static ClientState Client(TenantState tenant, Guid id, long window)
    {
        if (!tenant.Clients.TryGetValue(id, out var client))
        {
            tenant.Clients[id] = client = new ClientState(id) { Window = window };
        }

        if (client.Window != window)
        {
            if (client.Local > client.Flushed)
            {
                client.Pending.Add((client.Window, client.Local - client.Flushed));
            }

            client.Window = window;
            client.Local = 0;
            client.Flushed = 0;
            client.Remote = 0;
        }

        return client;
    }

    private sealed class TenantState(string code, DbDataSource source)
    {
        public string Code { get; } = code;

        public DbDataSource Source { get; set; } = source;

        public long Window { get; set; }

        public int Remote { get; set; }

        public Dictionary<Guid, ClientState> Clients { get; } = [];
    }

    private sealed class ClientState(Guid id)
    {
        public Guid Id { get; } = id;

        public long Window { get; set; }

        public int Local { get; set; }

        public int Flushed { get; set; }

        public int Remote { get; set; }

        public DateOnly? NoticeDay { get; set; }

        public List<(long Window, int Calls)> Pending { get; } = [];

        public Dictionary<DateOnly, DayDelta> Days { get; } = [];
    }

    private sealed class DayDelta
    {
        public int Calls { get; set; }

        public int Errors4xx { get; set; }

        public int Errors5xx { get; set; }

        public int RateLimited { get; set; }

        public int MaxMs { get; set; }

        public int[] Histogram { get; private set; } = new int[Bounds.Length];

        public bool IsEmpty => Calls == 0;

        public DayDelta Copy() => new() { Calls = Calls, Errors4xx = Errors4xx, Errors5xx = Errors5xx, RateLimited = RateLimited, MaxMs = MaxMs, Histogram = (int[])Histogram.Clone() };

        public void Subtract(DayDelta other)
        {
            Calls -= other.Calls;
            Errors4xx -= other.Errors4xx;
            Errors5xx -= other.Errors5xx;
            RateLimited -= other.RateLimited;
            for (var i = 0; i < Histogram.Length; i++)
            {
                Histogram[i] -= other.Histogram[i];
            }
        }
    }
}

/// <summary>The answer-time histogram: upper bound in milliseconds (as text) to the number of calls.</summary>
public static class LatencyHistogram
{
    public static string Merge(string? existing, int[] counts)
    {
        var node = existing is null ? new JsonObject() : JsonNode.Parse(existing) as JsonObject ?? new JsonObject();
        for (var i = 0; i < counts.Length; i++)
        {
            if (counts[i] <= 0)
            {
                continue;
            }

            var key = ApiUsageRecorder.Bounds[i].ToString(CultureInfo.InvariantCulture);
            node[key] = (node[key]?.GetValue<int>() ?? 0) + counts[i];
        }

        return node.ToJsonString();
    }

    /// <summary>The 95th percentile: the upper bound of the bucket holding the 95th-percentile call; the slowest answer for the open bucket; 0 for no calls.</summary>
    public static int Percentile95(string json, int maxMs)
    {
        var counts = new int[ApiUsageRecorder.Bounds.Length];
        if (JsonNode.Parse(json) is JsonObject node)
        {
            for (var i = 0; i < counts.Length; i++)
            {
                counts[i] = node[ApiUsageRecorder.Bounds[i].ToString(CultureInfo.InvariantCulture)]?.GetValue<int>() ?? 0;
            }
        }

        var total = counts.Sum();
        if (total == 0)
        {
            return 0;
        }

        var target = (int)Math.Ceiling(total * 0.95);
        var seen = 0;
        for (var i = 0; i < counts.Length; i++)
        {
            seen += counts[i];
            if (seen >= target)
            {
                return ApiUsageRecorder.Bounds[i] == int.MaxValue ? maxMs : Math.Min(ApiUsageRecorder.Bounds[i], Math.Max(maxMs, 1));
            }
        }

        return maxMs;
    }
}
