using System.Data.Common;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Infrastructure.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Jobs;

/// <summary>
/// Runs a module starts from its own service (slice S1.35b, contract <see cref="IJobRequests"/>): a queued run as the job centre's "run now" writes it
/// (log line and <c>kernel.job.run_requested</c> event), a cancel as the job centre's cancel, and the states of runs. In the caller's transaction when
/// one is given, else on a short connection of its own. The caller has checked its own permission.
/// </summary>
internal sealed class JobRequests(ITenantConnectionProvider tenant, IClock clock, JobCatalog catalog) : IJobRequests
{
    public async Task<Guid> QueueAsync(JobRequest request, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var spec = catalog.Find(request.Code) ?? throw new InvalidOperationException("Unknown job " + request.Code);
        return await OnAsync(transaction, async (c, tx) =>
        {
            var now = clock.UtcNow;
            var id = Guid.CreateVersion7(now);
            var inserted = await IdentityDb.ExecuteAsync(c, tx, """
                insert into job.runs (id, definition_id, company_id, status, priority, attempt, parameters, requested_by, actor_user_id, created_at, created_by, updated_at, updated_by, row_version)
                select $1, d.id, $3, 'queued', d.priority, 0, $4, $5, $6, $7, $5, $7, $5, 1 from job.definitions d where d.code = $2
                """, cancellationToken, id, request.Code, request.CompanyId, new JsonPayload(request.Parameters.GetRawText()), request.RequestedBy,
                spec.RunsAs == JobActors.Requester ? request.RequestedBy : null, now).ConfigureAwait(false);
            if (inserted == 0)
            {
                throw new InvalidOperationException("The job " + request.Code + " is not seeded for this customer.");
            }

            await JobStore.LogAsync(c, tx, id, "info", "queued by " + request.RequestedBy.ToString(), now, cancellationToken).ConfigureAwait(false);
            await JobStore.RaiseAsync(c, tx, JobEvents.RunRequested, "job.run", id, now, new { run_id = id, code = request.Code, company_id = request.CompanyId, by = request.RequestedBy }, cancellationToken).ConfigureAwait(false);
            return id;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> CancelAsync(IReadOnlyCollection<Guid> runIds, Guid by, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runIds);
        if (runIds.Count == 0)
        {
            return 0;
        }

        return await OnAsync(transaction, async (c, tx) =>
        {
            var now = clock.UtcNow;
            var asked = 0;
            foreach (var id in runIds)
            {
                // A queued run ends at once; a running run stops at its next chunk (FR-KRN-JOB-02), as the job centre's cancel.
                var changed = await IdentityDb.ExecuteAsync(c, tx, """
                    update job.runs set
                        status = case when status = 'queued' then 'cancelled' else status end,
                        finished_at = case when status = 'queued' then $2 else finished_at end,
                        cancel_requested_at = coalesce(cancel_requested_at, $2),
                        updated_at = $2, updated_by = $3, row_version = row_version + 1
                    where id = $1 and status in ('queued', 'running')
                    """, cancellationToken, id, now, by).ConfigureAwait(false);
                if (changed == 0)
                {
                    continue;
                }

                asked++;
                await JobStore.LogAsync(c, tx, id, "warning", "cancel asked by " + by.ToString(), now, cancellationToken).ConfigureAwait(false);
                await JobStore.RaiseAsync(c, tx, JobEvents.RunCancelRequested, "job.run", id, now, new { run_id = id, by }, cancellationToken).ConfigureAwait(false);
            }

            return asked;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<JobRunState>> StatesAsync(IReadOnlyCollection<Guid> runIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runIds);
        if (runIds.Count == 0)
        {
            return [];
        }

        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, cancellationToken).ConfigureAwait(false);
        await using var command = IdentityDb.Command(c, null, """
            select r.id, d.code, r.status, r.progress_pct, r.attempt, r.started_at, r.finished_at, r.cancel_requested_at, r.error
            from job.runs r join job.definitions d on d.id = r.definition_id where r.id = any($1) order by r.id
            """, runIds.ToArray());
        var list = new List<JobRunState>();
        await using var r = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new JobRunState(
                r.GetGuid(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetDecimal(3), r.GetInt32(4),
                r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5), r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6),
                r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7), r.IsDBNull(8) ? null : r.GetString(8)));
        }

        return list;
    }

    private async Task<T> OnAsync<T>(DbTransaction? transaction, Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work, CancellationToken ct)
    {
        if (transaction is not null)
        {
            if (transaction is not NpgsqlTransaction tx || tx.Connection is not { } callerConnection)
            {
                throw new ArgumentException("A job request in a transaction needs the caller's open PostgreSQL transaction.", nameof(transaction));
            }

            return await work(callerConnection, tx).ConfigureAwait(false);
        }

        await using var c = await IdentityDb.OpenAsync(tenant.DataSource, ct).ConfigureAwait(false);
        await using var own = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        var result = await work(c, own).ConfigureAwait(false);
        await own.CommitAsync(ct).ConfigureAwait(false);
        return result;
    }
}
