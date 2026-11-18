using System.Text.Json;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.ControlPlane.Tenancy;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Infrastructure.ControlPlane;
using Erp.Kernel.Infrastructure.ControlPlane.Tenancy;
using Erp.Kernel.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Jobs;

/// <summary>Settings of the dispatcher (slice S1.11 ruling Q7): events read per schema and pass (<c>Events:BatchSize</c>, 100).</summary>
public sealed record EventDispatcherOptions(int BatchSize = 100);

/// <summary>What one pass did for one customer.</summary>
public sealed record EventDispatchOutcome(string Code, int Delivered, int Failed, string? Error);

/// <summary>
/// The event dispatcher (spec 03 section 7.5; slice S1.11 Build item 4, ruling Q7), run by the worker host every
/// <c>Events:PollMilliseconds</c> (2000). For every customer database and every schema with an outbox it takes the
/// unpublished events whose wait is over, oldest first, locked so that two dispatchers never take the same event. Each
/// event goes to every consumer registered for its type: the delivery is recorded in that schema's inbox in the same
/// transaction as the consumer's work, so a consumer never handles an event twice, even when the event is read again.
/// When every consumer succeeded the event is marked published; otherwise it waits (10 seconds, growing to 1 hour) and is
/// tried again, the consumers that already succeeded skipping it. Events are never dropped; the alert job reports events
/// waiting more than 5 minutes and consumers failing for an hour. Events go out in every access mode (licence e-mails
/// matter most when a licence runs out).
/// </summary>
public sealed class EventDispatcher(ControlPlaneOptions controlPlane, ITenantDataSourceFactory dataSources, ILicenceResolver licences, IServiceScopeFactory scopes, IClock clock, EventDispatcherOptions options)
{
    private static readonly int[] BackoffSeconds = [10, 30, 60, 300, 900, 1800, 3600];

    /// <summary>The wait after the <paramref name="attempts"/>-th failed delivery (10 seconds growing to 1 hour, ruling Q7).</summary>
    public static TimeSpan Backoff(int attempts) => TimeSpan.FromSeconds(BackoffSeconds[Math.Clamp(attempts - 1, 0, BackoffSeconds.Length - 1)]);

    public async Task<IReadOnlyList<EventDispatchOutcome>> DispatchOnceAsync(string? onlyTenantCode, CancellationToken cancellationToken)
    {
        var outcomes = new List<EventDispatchOutcome>();
        foreach (var tenant in await ServedTenants.ListAsync(controlPlane, onlyTenantCode, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                outcomes.Add(await DispatchTenantAsync(tenant, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                outcomes.Add(new EventDispatchOutcome(tenant.Code, 0, 0, ex.Message));
            }
        }

        return outcomes;
    }

    private async Task<EventDispatchOutcome> DispatchTenantAsync(ResolvedTenant tenant, CancellationToken ct)
    {
        var dataSource = await dataSources.GetAsync(tenant, ct).ConfigureAwait(false);
        var licence = await licences.ResolveAsync(tenant, dataSource, ct).ConfigureAwait(false);
        var mode = TenantAccessPolicy.Decide(tenant.Status, tenant.DatabaseStatus, licence.State);
        if (mode is TenantAccessMode.NotFound or TenantAccessMode.Unavailable)
        {
            return new EventDispatchOutcome(tenant.Code, 0, 0, null);
        }

        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Bind(tenant, mode, dataSource, licence);
        var consumers = scope.ServiceProvider.GetServices<IEventConsumer>().ToList();
        List<string> schemas;
        await using (var c = (NpgsqlConnection)await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            schemas = await OpsAlerts.OutboxSchemasAsync(c, ct).ConfigureAwait(false);
        }

        int delivered = 0, failed = 0;
        foreach (var schema in schemas)
        {
            var (d, f) = await DispatchSchemaAsync(dataSource, schema, consumers, ct).ConfigureAwait(false);
            delivered += d;
            failed += f;
        }

        return new EventDispatchOutcome(tenant.Code, delivered, failed, null);
    }

    private async Task<(int Delivered, int Failed)> DispatchSchemaAsync(System.Data.Common.DbDataSource dataSource, string schema, IReadOnlyList<IEventConsumer> consumers, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var table = OpsAlerts.Quote(schema);
        await using var c = (NpgsqlConnection)await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        var events = new List<(OutboxEvent Event, int Attempts)>();
#pragma warning disable CA2100 // the schema name comes from the catalog and is quoted; values are parameters
        await using (var command = IdentityDb.Command(c, tx, $"""
            select id, occurred_at, event_type, aggregate_type, aggregate_id, payload::text, headers::text, attempts from {table}.outbox_messages
            where published_at is null and (next_attempt_at is null or next_attempt_at <= $1)
            order by occurred_at, id limit $2 for update skip locked
            """, now, options.BatchSize))
#pragma warning restore CA2100
        await using (var r = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                using var payload = JsonDocument.Parse(r.GetString(5));
                using var headers = JsonDocument.Parse(r.GetString(6));
                events.Add((new OutboxEvent(r.GetGuid(0), schema, r.GetString(2), r.GetString(3), r.GetGuid(4), r.GetFieldValue<DateTimeOffset>(1), payload.RootElement.Clone(), headers.RootElement.Clone()), r.GetInt32(7)));
            }
        }

        int delivered = 0, failed = 0;
        foreach (var (message, attempts) in events)
        {
            string? error = null;
            foreach (var consumer in consumers.Where(x => x.Accepts(message.EventType)))
            {
                await tx.SaveAsync("delivery", ct).ConfigureAwait(false);
                try
                {
#pragma warning disable CA2100
                    var first = await IdentityDb.ExecuteAsync(c, tx, $"""
                        insert into {table}.inbox_messages (message_id, consumer, processed_at) values ($1, $2, $3) on conflict do nothing
                        """, ct, message.Id, consumer.Name, now).ConfigureAwait(false) == 1;
#pragma warning restore CA2100
                    if (first)
                    {
                        await consumer.HandleAsync(message, c, tx, ct).ConfigureAwait(false);
                    }

                    await tx.ReleaseAsync("delivery", ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    await tx.RollbackAsync("delivery", ct).ConfigureAwait(false);
                    error = (error is null ? string.Empty : error + "; ") + consumer.Name + ": " + ex.Message;
                }
            }

#pragma warning disable CA2100
            if (error is null)
            {
                await IdentityDb.ExecuteAsync(c, tx, $"update {table}.outbox_messages set published_at = $2, next_attempt_at = null where id = $1", ct, message.Id, now).ConfigureAwait(false);
                delivered++;
            }
            else
            {
                await IdentityDb.ExecuteAsync(c, tx, $"update {table}.outbox_messages set attempts = attempts + 1, next_attempt_at = $2, last_error = $3 where id = $1", ct,
                    message.Id, now + Backoff(attempts + 1), error.Length > 4000 ? error[..4000] : error).ConfigureAwait(false);
                failed++;
            }
#pragma warning restore CA2100
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return (delivered, failed);
    }
}
