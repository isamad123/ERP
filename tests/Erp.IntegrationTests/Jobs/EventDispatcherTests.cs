using System.Collections.Concurrent;
using System.Data.Common;
using Erp.IntegrationTests.Tenancy;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Erp.IntegrationTests.Jobs;

/// <summary>
/// The event dispatcher of slice S1.11a (Build item 4; spec 03 section 7.5; ruling Q7): every schema's outbox in each
/// customer database, the inbox making each consumer handle an event once even when it is read twice or by two
/// dispatchers, retry with growing waits, never dropping an event, and the hrm outbox included.
/// </summary>
[Collection(TenancyGroup.Name)]
public sealed class EventDispatcherTests(TenancyFixture fx) : IAsyncLifetime
{
    private JobRig _rig = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _rig = await JobRig.StartAsync(fx, "je", Ct);

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    /// <summary>A consumer of test.* events that writes a row in the dispatcher's transaction and can be told to fail.</summary>
    private sealed class RecordingConsumer(string name) : IEventConsumer
    {
        public ConcurrentDictionary<Guid, int> Handled { get; } = new();

        public int FailuresLeft { get; set; }

        public string Name => name;

        public bool Accepts(string eventType) => eventType.StartsWith("test.", StringComparison.Ordinal);

        public async Task HandleAsync(OutboxEvent message, DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
        {
            await using (var command = new NpgsqlCommand("insert into job_test.rows (run_id, n) values ($1, $2)", (NpgsqlConnection)connection, (NpgsqlTransaction)transaction))
            {
                command.Parameters.Add(new NpgsqlParameter { Value = message.Id });
                command.Parameters.Add(new NpgsqlParameter { Value = name == "test.first" ? 1 : 2 });
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            if (FailuresLeft > 0)
            {
                FailuresLeft--;
                throw new InvalidOperationException("consumer refused on purpose");
            }

            Handled.AddOrUpdate(message.Id, 1, (_, n) => n + 1);
        }
    }

    private (EventDispatcher Dispatcher, RecordingConsumer First, RecordingConsumer Second) Dispatcher(RecordingConsumer? first = null, RecordingConsumer? second = null)
    {
        first ??= new RecordingConsumer("test.first");
        second ??= new RecordingConsumer("test.second");
        var provider = _rig.Provider(extra: s =>
        {
            s.AddSingleton<IEventConsumer>(first);
            s.AddSingleton<IEventConsumer>(second);
        });
        return (provider.GetRequiredService<EventDispatcher>(), first, second);
    }

    private async Task<Guid> WriteEventAsync(string schema, string type)
    {
        var id = Guid.CreateVersion7();
#pragma warning disable CA2100 // test SQL with a fixed schema name
        await _rig.ExecAsync($"insert into {schema}.outbox_messages (id, occurred_at, event_type, aggregate_type, aggregate_id, payload) values ($1, $2, $3, 'test', $1, '{{}}')",
            Ct, null, id, _rig.Clock.UtcNow, type);
#pragma warning restore CA2100
        return id;
    }

    private async Task DispatchAsync(EventDispatcher dispatcher)
    {
        foreach (var outcome in await dispatcher.DispatchOnceAsync(_rig.Code, Ct))
        {
            Assert.True(outcome.Error is null, outcome.Error);
        }
    }

    [Fact]
    public async Task S111a_Spec03_7_5_an_event_of_any_schema_reaches_each_consumer_once_even_when_dispatched_again_or_by_two_dispatchers()
    {
        var (one, first, second) = Dispatcher();
        var (two, _, _) = Dispatcher(first, second);
        var events = new List<Guid> { await WriteEventAsync("wf", "test.workflow"), await WriteEventAsync("hrm", "test.hr"), await WriteEventAsync("job", "test.job") };
        var other = await WriteEventAsync("iam", "iam.not_for_them");

        await Task.WhenAll(DispatchAsync(one), DispatchAsync(two));
        await DispatchAsync(one);

        foreach (var e in events)
        {
            Assert.Equal(1, first.Handled[e]);
            Assert.Equal(1, second.Handled[e]);
            Assert.Equal(2, await _rig.ScalarAsync<long>("select count(*) from job_test.rows where run_id = $1", Ct, null, e));
        }

        Assert.True(await _rig.ScalarAsync<bool>("select published_at is not null from wf.outbox_messages where id = $1", Ct, null, events[0]));
        Assert.True(await _rig.ScalarAsync<bool>("select published_at is not null from hrm.outbox_messages where id = $1", Ct, null, events[1]));
        Assert.Equal(2, await _rig.ScalarAsync<long>("select count(*) from hrm.inbox_messages where message_id = $1", Ct, null, events[1]));

        // An event nobody consumes is published at once and recorded in no inbox.
        Assert.True(await _rig.ScalarAsync<bool>("select published_at is not null from iam.outbox_messages where id = $1", Ct, null, other));
        Assert.Equal(0, await _rig.ScalarAsync<long>("select count(*) from iam.inbox_messages where message_id = $1", Ct, null, other));

        // Read again from the outbox (published flag cleared by hand): the inbox stops both consumers.
        await _rig.ExecAsync("update wf.outbox_messages set published_at = null where id = $1", Ct, null, events[0]);
        await DispatchAsync(one);
        Assert.Equal(1, first.Handled[events[0]]);
        Assert.Equal(2, await _rig.ScalarAsync<long>("select count(*) from job_test.rows where run_id = $1", Ct, null, events[0]));
    }

    [Fact]
    public async Task S111a_Q7_a_failing_consumer_is_retried_with_growing_waits_never_dropped_and_the_other_consumer_is_not_repeated()
    {
        var failing = new RecordingConsumer("test.second") { FailuresLeft = 2 };
        var (dispatcher, first, second) = Dispatcher(second: failing);
        var e = await WriteEventAsync("wf", "test.retry");

        await DispatchAsync(dispatcher);
        Assert.Equal(1, first.Handled[e]);
        Assert.False(second.Handled.ContainsKey(e));
        Assert.Equal(1, await _rig.ScalarAsync<int>("select attempts from wf.outbox_messages where id = $1", Ct, null, e));
        Assert.Contains("consumer refused on purpose", await _rig.ScalarAsync<string>("select last_error from wf.outbox_messages where id = $1", Ct, null, e), StringComparison.Ordinal);
        Assert.Equal(_rig.Clock.UtcNow.AddSeconds(10), await _rig.ScalarAsync<DateTimeOffset>("select next_attempt_at from wf.outbox_messages where id = $1", Ct, null, e), TimeSpan.FromSeconds(1));
        // The failed consumer's own work was rolled back with its inbox row.
        Assert.Equal(1, await _rig.ScalarAsync<long>("select count(*) from job_test.rows where run_id = $1", Ct, null, e));

        // Not before its wait; after a second failure the wait grows to 30 seconds.
        await DispatchAsync(dispatcher);
        Assert.Equal(1, await _rig.ScalarAsync<int>("select attempts from wf.outbox_messages where id = $1", Ct, null, e));
        _rig.Clock.Advance(TimeSpan.FromSeconds(11));
        await DispatchAsync(dispatcher);
        Assert.Equal(2, await _rig.ScalarAsync<int>("select attempts from wf.outbox_messages where id = $1", Ct, null, e));
        Assert.Equal(_rig.Clock.UtcNow.AddSeconds(30), await _rig.ScalarAsync<DateTimeOffset>("select next_attempt_at from wf.outbox_messages where id = $1", Ct, null, e), TimeSpan.FromSeconds(1));

        _rig.Clock.Advance(TimeSpan.FromSeconds(31));
        await DispatchAsync(dispatcher);
        Assert.Equal(1, first.Handled[e]); // never repeated
        Assert.Equal(1, second.Handled[e]);
        Assert.True(await _rig.ScalarAsync<bool>("select published_at is not null from wf.outbox_messages where id = $1", Ct, null, e));
        Assert.Equal(2, await _rig.ScalarAsync<long>("select count(*) from job_test.rows where run_id = $1", Ct, null, e));
        Assert.Equal(TimeSpan.FromHours(1), EventDispatcher.Backoff(50)); // the wait stops growing at one hour
    }
}
