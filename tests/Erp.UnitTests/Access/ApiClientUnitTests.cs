using System.Globalization;
using System.Net;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Infrastructure.Access;
using Npgsql;

namespace Erp.UnitTests.Access;

/// <summary>Pure rules of API clients (slice S1.27c): key format, address ranges, the answer-time histogram and the rate windows. FR-ADM-SEC-02, FR-KRN-INT-07.</summary>
public sealed class ApiClientUnitTests
{
    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset Now { get; set; } = now;

        public DateTimeOffset UtcNow => Now;
    }

    [Fact]
    public void FR_ADM_SEC_02_a_key_is_the_client_id_a_dot_and_the_secret_and_nothing_else_parses()
    {
        var (clientId, secret) = ApiKeys.NewKey();
        Assert.StartsWith("ak_", clientId, StringComparison.Ordinal);
        Assert.Equal(27, clientId.Length);
        Assert.Equal(43, secret.Length); // 32 random bytes, base64url
        Assert.DoesNotContain('.', secret);
        Assert.True(ApiKeys.TryParse(clientId + "." + secret, out var c, out var s));
        Assert.Equal((clientId, secret), (c, s));
        Assert.NotEqual(secret, ApiKeys.NewSecret());

        foreach (var bad in new[] { null, "", "ak_x", "ak_x.", ".secret", "other_x.secret", new string('a', 300) })
        {
            Assert.False(ApiKeys.TryParse(bad, out _, out _), bad);
        }
    }

    [Theory]
    [InlineData("10.1.2.3", "10.1.2.3")]
    [InlineData("10.1.2.0/24", "10.1.2.0/24")]
    [InlineData("10.1.2.77/24", "10.1.2.0/24")]
    [InlineData("::ffff:10.1.2.3", "10.1.2.3")]
    [InlineData("2001:db8::1", "2001:db8::1")]
    public void FR_ADM_SEC_02_allow_list_entries_are_normalized(string text, string expected)
    {
        Assert.True(IpRanges.TryNormalize(text, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.256")]
    [InlineData("fe80::1%eth0")]
    [InlineData("10.0.0.1/abc")]
    public void FR_ADM_SEC_02_an_invalid_allow_list_entry_is_refused(string text) => Assert.False(IpRanges.TryNormalize(text, out _));

    [Fact]
    public void FR_ADM_SEC_02_the_allow_list_matches_by_address_and_an_empty_or_missing_caller_matches_nothing()
    {
        string[] list = ["10.1.2.0/24", "203.0.113.7", "2001:db8::/32"];
        Assert.True(IpRanges.Allows(list, IPAddress.Parse("10.1.2.200")));
        Assert.True(IpRanges.Allows(list, IPAddress.Parse("::ffff:10.1.2.200")));
        Assert.True(IpRanges.Allows(list, IPAddress.Parse("203.0.113.7")));
        Assert.True(IpRanges.Allows(list, IPAddress.Parse("2001:db8:ffff::1")));
        Assert.False(IpRanges.Allows(list, IPAddress.Parse("10.1.3.1")));
        Assert.False(IpRanges.Allows(list, IPAddress.Parse("203.0.113.8")));
        Assert.False(IpRanges.Allows(list, IPAddress.Parse("2001:db9::1")));
        Assert.False(IpRanges.Allows(list, null));
        Assert.False(IpRanges.Allows([], IPAddress.Parse("10.1.2.3")));
    }

    [Fact]
    public void FR_ADM_SEC_02_the_95th_percentile_is_the_upper_bound_of_its_bucket_and_never_above_the_slowest_answer()
    {
        var counts = new int[ApiUsageRecorder.Bounds.Length];
        counts[Array.IndexOf(ApiUsageRecorder.Bounds, 25)] = 90;
        counts[Array.IndexOf(ApiUsageRecorder.Bounds, 250)] = 10;
        var json = LatencyHistogram.Merge(null, counts);
        Assert.Equal(240, LatencyHistogram.Percentile95(json, 240)); // 95 of 100 calls fall in the 250 ms bucket, but nothing was slower than 240 ms
        Assert.Equal(250, LatencyHistogram.Percentile95(json, 900)); // with a slower answer, the bucket bound
        Assert.Equal(100, LatencyHistogram.Percentile95(LatencyHistogram.Merge(null, With(100, 10)), 100)); // bucket bound 100, slowest 100
        Assert.Equal(7, LatencyHistogram.Percentile95(LatencyHistogram.Merge(null, With(10, 10)), 7)); // bound 10 but the slowest was 7
        Assert.Equal(30_000, LatencyHistogram.Percentile95(LatencyHistogram.Merge(null, With(int.MaxValue, 5)), 30_000)); // the open bucket reads the slowest
        Assert.Equal(0, LatencyHistogram.Percentile95("{}", 0));

        var merged = LatencyHistogram.Merge(json, With(25, 5));
        Assert.Equal(95 + 10, System.Text.Json.Nodes.JsonNode.Parse(merged)!.AsObject().Sum(p => (int)p.Value!));
        Assert.Equal(95, (int)System.Text.Json.Nodes.JsonNode.Parse(merged)!["25"]!);
    }

    private static int[] With(int bound, int calls)
    {
        var counts = new int[ApiUsageRecorder.Bounds.Length];
        counts[Array.IndexOf(ApiUsageRecorder.Bounds, bound)] = calls;
        return counts;
    }

    [Fact]
    public void FR_KRN_INT_07_a_fixed_window_counts_per_client_per_minute_and_the_customer_ceiling_counts_all_clients()
    {
        var clock = new FixedClock(DateTimeOffset.Parse("2026-11-25T10:00:05Z", CultureInfo.InvariantCulture));
        var recorder = new ApiUsageRecorder(clock);
        using var source = NpgsqlDataSource.Create("Host=nowhere");
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();

        for (var i = 0; i < 3; i++)
        {
            Assert.True(recorder.TryAcquire("t", source, a, 3, 100).Allowed);
        }

        var refused = recorder.TryAcquire("t", source, a, 3, 100);
        Assert.False(refused.Allowed);
        Assert.True(refused.ClientLimit);
        Assert.Equal(55, refused.RetryAfterSeconds);
        Assert.True(recorder.TryAcquire("t", source, b, 3, 100).Allowed); // another client has its own count

        // a customer ceiling of 4 across both clients: 3 + 1 so far
        var third = recorder.TryAcquire("t", source, b, 10, 4);
        Assert.False(third.Allowed);
        Assert.False(third.ClientLimit);

        clock.Now = clock.Now.AddMinutes(1);
        Assert.True(recorder.TryAcquire("t", source, a, 3, 100).Allowed); // a new window
        Assert.True(recorder.TryAcquire("other", source, a, 3, 1).Allowed); // another customer's counters are separate
    }

    [Fact]
    public void FR_KRN_INT_07_the_first_refusal_of_the_day_asks_for_a_notice_once_per_host()
    {
        var clock = new FixedClock(DateTimeOffset.Parse("2026-11-25T10:00:05Z", CultureInfo.InvariantCulture));
        var recorder = new ApiUsageRecorder(clock);
        using var source = NpgsqlDataSource.Create("Host=nowhere");
        var a = Guid.CreateVersion7();
        Assert.False(recorder.FirstRefusalToday("t", a)); // nothing counted yet
        recorder.TryAcquire("t", source, a, 1, 10);
        Assert.True(recorder.FirstRefusalToday("t", a));
        Assert.False(recorder.FirstRefusalToday("t", a));
        clock.Now = clock.Now.AddDays(1);
        Assert.True(recorder.FirstRefusalToday("t", a));
    }

    [Fact]
    public void FR_ADM_SEC_02_the_verification_cache_matches_only_the_same_key_against_the_same_stored_hash()
    {
        var cache = new ApiKeyVerificationCache();
        Assert.False(cache.Matches("ak_x.secret", "hash1"));
        cache.Remember("ak_x.secret", "hash1");
        Assert.True(cache.Matches("ak_x.secret", "hash1"));
        Assert.False(cache.Matches("ak_x.secret", "hash2")); // the row's hash changed (rotation): the slow check runs again
        Assert.False(cache.Matches("ak_x.other", "hash1"));
    }

    [Fact]
    public void FR_ADM_SEC_02_refusals_of_one_kind_are_recorded_once_a_minute()
    {
        var clock = new FixedClock(DateTimeOffset.Parse("2026-11-25T10:00:05Z", CultureInfo.InvariantCulture));
        var throttle = new ApiKeyRefusalThrottle(clock);
        Assert.True(throttle.ShouldRecord("a"));
        Assert.False(throttle.ShouldRecord("a"));
        Assert.True(throttle.ShouldRecord("b"));
        clock.Now = clock.Now.AddMinutes(1);
        Assert.True(throttle.ShouldRecord("a"));
    }
}
