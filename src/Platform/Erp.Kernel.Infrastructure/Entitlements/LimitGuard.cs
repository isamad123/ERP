using System.Data.Common;
using System.Text.Json.Nodes;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.ControlPlane.Tenancy;
using Erp.Kernel.Contracts.Entitlements;
using Erp.Kernel.Contracts.Generated;
using Erp.Kernel.Contracts.Settings;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Entitlements;

/// <summary>
/// The limit warnings of one request (ruling Q7: "returned with the response"); the API host writes them to the
/// <see cref="LimitHeaders.Warning"/> header.
/// </summary>
public sealed class LimitWarnings : ILimitWarningSource
{
    private readonly List<LimitDecision> _items = [];

    public IReadOnlyList<LimitDecision> Warnings => _items;

    public void Add(LimitDecision decision) => _items.Add(decision);
}

/// <summary>
/// The kernel limit guard (FR-KRN-ENT-05, FR-KRN-IAM-18, FR-ADM-USR-08; slice S1.09 ruling Q7). The limit comes from the
/// entitlement in force for the request; a missing limit means no limit and costs nothing. Otherwise a transaction-level
/// lock per limit (the customer has its own database, so per customer and limit) serializes the check: two administrators
/// taking the last seat at the same time cannot both succeed. A holder who already has a seat is never refused. A seat
/// that crosses the warning threshold (<c>cfg.limits.warning_percent</c>) is announced once, in the caller's transaction;
/// a refusal is recorded in a transaction of its own (the caller's is rolled back) and thrown.
/// </summary>
public sealed class LimitGuard(
    ICurrentLicence licence,
    IEnumerable<ILimitCounter> counters,
    ISettings settings,
    ITenantConnectionProvider connections,
    ICurrentActor actor,
    IClock clock,
    LimitWarnings warnings) : ILimitGuard
{
    public async Task<LimitDecision> TakeAsync(string limitKey, DbTransaction transaction, Guid? holder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var counter = counters.FirstOrDefault(c => c.LimitKey == limitKey) ?? throw new InvalidOperationException($"No counter is registered for limit {limitKey}.");
        var max = licence.Licence.Claims?.Limit(limitKey);
        if (max is null)
        {
            return new LimitDecision(limitKey, null, 0, Allowed: true, Warning: false, Crossed: false, 0);
        }

        var percent = await WarningPercentAsync(cancellationToken).ConfigureAwait(false);
        await IdentityDb.ExecuteAsync((NpgsqlConnection)transaction.Connection!, (NpgsqlTransaction)transaction,
            "select pg_advisory_xact_lock(hashtext('licence.limit.' || $1))", cancellationToken, limitKey).ConfigureAwait(false);
        var count = await counter.CountAsync(transaction, holder, cancellationToken).ConfigureAwait(false);
        if (count.HolderCounted)
        {
            return Remember(new LimitDecision(limitKey, max, count.Used, true, AtWarning(count.Used, max.Value, percent), false, percent));
        }

        var now = clock.UtcNow;
        if (count.Used >= max.Value)
        {
            var refused = new LimitDecision(limitKey, max, count.Used, Allowed: false, Warning: true, Crossed: false, percent);
            await RecordRefusalAsync(refused, holder, now, cancellationToken).ConfigureAwait(false);
            throw new LimitReachedException(refused);
        }

        var after = count.Used + 1;
        var decision = new LimitDecision(limitKey, max, after, true, AtWarning(after, max.Value, percent), AtWarning(after, max.Value, percent) && !AtWarning(count.Used, max.Value, percent), percent);
        if (decision.Crossed)
        {
            await CustomerLicence.RecordAsync((NpgsqlConnection)transaction.Connection!, (NpgsqlTransaction)transaction, LicenceEvents.LimitWarning,
                Audit.Severities.Warning, Details(decision), ActorId(limitKey, holder), now, cancellationToken).ConfigureAwait(false);
        }

        return Remember(decision);
    }

    private static bool AtWarning(long used, long max, int percent) => max == 0 || used * 100 >= max * percent;

    private LimitDecision Remember(LimitDecision decision)
    {
        if (decision.Warning)
        {
            warnings.Add(decision);
        }

        return decision;
    }

    private async Task<int> WarningPercentAsync(CancellationToken cancellationToken)
    {
        var value = await settings.GetAsync(KernelSettingKeys.CfgLimitsWarningPercent, SettingContext.Customer, cancellationToken).ConfigureAwait(false);
        return value.Value.TryGetInt32(out var p) ? p : 90;
    }

    private async Task RecordRefusalAsync(LimitDecision refused, Guid? holder, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var c = await IdentityDb.OpenAsync(connections.DataSource, cancellationToken).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // The refused seat's holder is named, and is the actor of an anonymous step (activation, sign-in): S1.09b review finding 5.
        var details = Details(refused);
        details["holder"] = holder?.ToString();
        await CustomerLicence.RecordAsync(c, tx, LicenceEvents.LimitReached, Audit.Severities.Warning, details, ActorId(refused.LimitKey, holder), now, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static JsonObject Details(LimitDecision d) => new()
    {
        ["limit"] = d.LimitKey,
        ["used"] = d.Used,
        ["max"] = d.Max,
        ["percent"] = d.Percent,
        ["warning_percent"] = d.WarningPercent,
    };

    /// <summary>The signed-in caller; for a sign-in step (no caller yet) the user taking the seat, else the system user.</summary>
    private Guid ActorId(string limitKey, Guid? holder)
    {
        try
        {
            return actor.UserId;
        }
        catch (InvalidOperationException)
        {
            // Only a user limit's holder is a person; a company's id is never an actor.
            return holder is { } h && limitKey is LimitKeys.NamedUsers or LimitKeys.ConcurrentUsers ? h : BaselineSeed.SystemUserId;
        }
    }
}
