using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using Erp.Kernel.Infrastructure.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Settings;

/// <summary>Options of the settings cache: <c>Settings:RefreshMilliseconds</c> (default 1000; 0 checks on every read).</summary>
public sealed record SettingsCacheOptions(TimeSpan RefreshInterval);

/// <summary>
/// One customer's settings and flags as last loaded: definitions, every value except user values (loaded per user on first
/// use), flags and overrides, with the change counters they were read at.
/// </summary>
public sealed class SettingsSnapshot
{
    public required long SettingsVersion { get; init; }

    public required long FlagsVersion { get; init; }

    public required IReadOnlyDictionary<string, SettingRow> Definitions { get; init; }

    /// <summary>Pack, tenant, company and role values by setting key.</summary>
    public required ILookup<string, SettingValueRow> Values { get; init; }

    public required IReadOnlyDictionary<string, FlagRow> Flags { get; init; }

    public required ILookup<string, FlagOverrideRow> Overrides { get; init; }

    /// <summary>User values by user, filled on first use; dropped with the snapshot when anything changes.</summary>
    public ConcurrentDictionary<Guid, ILookup<string, SettingValueRow>> Users { get; } = new();
}

/// <summary>
/// The settings cache of a host (FR-KRN-SET-06; slice S1.09 ruling Q13 option A). Each customer's snapshot is used as it is
/// until the refresh interval has passed; then the counters in cfg.config_versions are read (one indexed one-row read) and
/// the snapshot is reloaded only when one moved. Database triggers raise the counters on every change, from any host or
/// path, so a change is in force everywhere within about one interval; the host that made it drops its snapshot at once.
/// Counters and values are read in one repeatable-read transaction, so a snapshot never mixes two versions. Only reads
/// that arrive cost anything; timing uses <see cref="TimeProvider"/> (machine time, not business time).
/// </summary>
public sealed class SettingsCache(SettingsCacheOptions options, TimeProvider time)
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public async Task<SettingsSnapshot> GetAsync(string tenantCode, DbDataSource dataSource, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        var entry = _entries.GetOrAdd(tenantCode, _ => new Entry());
        var now = time.GetTimestamp();
        if (entry.Current is { } fresh && time.GetElapsedTime(Interlocked.Read(ref entry.CheckedAt), now) < options.RefreshInterval && options.RefreshInterval > TimeSpan.Zero)
        {
            return fresh;
        }

        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var c = await IdentityDb.OpenAsync(dataSource, cancellationToken).ConfigureAwait(false);
            if (entry.Current is { } current)
            {
                var (settings, flags) = await SettingsStore.VersionsAsync(c, null, cancellationToken).ConfigureAwait(false);
                if (settings == current.SettingsVersion && flags == current.FlagsVersion)
                {
                    Interlocked.Exchange(ref entry.CheckedAt, now);
                    return current;
                }
            }

            var loaded = await LoadAsync(c, cancellationToken).ConfigureAwait(false);
            entry.Current = loaded;
            Interlocked.Exchange(ref entry.CheckedAt, now);
            return loaded;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>Drops the customer's snapshot (the host that changed a value sees it at once).</summary>
    public void Invalidate(string tenantCode)
    {
        if (_entries.TryGetValue(tenantCode, out var entry))
        {
            entry.Current = null;
        }
    }

    /// <summary>The user's values in the snapshot, read on first use.</summary>
    public static async Task<ILookup<string, SettingValueRow>> UserValuesAsync(SettingsSnapshot snapshot, DbDataSource dataSource, Guid userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Users.TryGetValue(userId, out var cached))
        {
            return cached;
        }

        await using var c = await IdentityDb.OpenAsync(dataSource, cancellationToken).ConfigureAwait(false);
        var values = (await SettingsStore.UserValuesAsync(c, null, userId, cancellationToken).ConfigureAwait(false)).ToLookup(v => v.Key, StringComparer.Ordinal);
        return snapshot.Users.GetOrAdd(userId, values);
    }

    private static async Task<SettingsSnapshot> LoadAsync(NpgsqlConnection c, CancellationToken ct)
    {
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct).ConfigureAwait(false);
        var (settings, flags) = await SettingsStore.VersionsAsync(c, tx, ct).ConfigureAwait(false);
        var snapshot = new SettingsSnapshot
        {
            SettingsVersion = settings,
            FlagsVersion = flags,
            Definitions = await SettingsStore.DefinitionsAsync(c, tx, null, ct).ConfigureAwait(false),
            Values = (await SettingsStore.ValuesAsync(c, tx, null, null, [], null, ct, allCompanies: true, allRoles: true).ConfigureAwait(false)).ToLookup(v => v.Key, StringComparer.Ordinal),
            Flags = await SettingsStore.FlagsAsync(c, tx, ct).ConfigureAwait(false),
            Overrides = (await SettingsStore.FlagOverridesAsync(c, tx, null, ct).ConfigureAwait(false)).ToLookup(o => o.Key, StringComparer.Ordinal),
        };
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return snapshot;
    }

    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public volatile SettingsSnapshot? Current;

        public long CheckedAt;
    }
}
