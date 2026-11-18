using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Jobs;

/// <summary>
/// Job set-up of a customer database (slice S1.11 Build item 1), run by provisioning, by the migrator's <c>migrate</c> and
/// when a licence adds modules, after the workflow set-up: every job of the installed modules' manifests gets its row in
/// <c>job.definitions</c>, keyed by code. The product's fields (module, parameters, limits, retry, priority) follow the
/// manifest on every run. The schedule and on/off choice come from the manifest only until a customer changes them
/// (the row was last written by someone other than the system user); from then on an upgrade keeps them.
/// Seeded rows are baseline data, like baseline roles and default routes, and are not change-logged.
/// </summary>
public static class JobSetup
{
    /// <returns>The number of definitions created.</returns>
    public static Task<int> EnsureAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, IReadOnlyList<string> manifestFolders, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifestFolders);
        var specs = JobManifest.FromFolders(manifestFolders);
        if (!specs.Any(s => s.Module == "kernel"))
        {
            specs = [.. JobManifest.Parse(JobManifest.KernelJson), .. specs];
        }

        return EnsureAsync(connection, transaction, specs, now, cancellationToken);
    }

    /// <returns>The number of definitions created.</returns>
    public static async Task<int> EnsureAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, IEnumerable<JobSpec> specs, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(specs);
        var problems = specs.SelectMany(s => JobManifest.Check(s).Select(p => $"{s.Code}: {p}")).ToList();
        if (problems.Count > 0)
        {
            throw new InvalidDataException("Job manifest problems: " + string.Join("; ", problems));
        }

        var own = transaction is null;
        var tx = transaction ?? await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var system = BaselineSeed.SystemUserId;
            await IdentityDb.ExecuteAsync(connection, tx, "select pg_advisory_xact_lock(hashtext('job.job_setup'))", cancellationToken).ConfigureAwait(false);
            var created = 0;
            foreach (var spec in specs.OrderBy(s => s.Code, StringComparer.Ordinal))
            {
                var schedule = spec.Schedule is { } s ? new JsonPayload(s.GetRawText()) : null;
                created += await IdentityDb.ExecuteAsync(connection, tx, """
                    insert into job.definitions (id, code, module_id, schedule, parameters, concurrency_limit, timeout_seconds, retry_policy, is_enabled,
                        priority, source, created_at, created_by, updated_at, updated_by, row_version)
                    values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, 'product', $11, $12, $11, $12, 1)
                    on conflict (code) do nothing
                    """, cancellationToken, Guid.CreateVersion7(now), spec.Code, spec.Module, schedule, new JsonPayload(spec.ParametersJson), spec.Concurrency,
                    spec.TimeoutSeconds, new JsonPayload(spec.RetryPolicyJson), spec.EnabledByDefault, spec.Priority, now, system).ConfigureAwait(false);

                // Product fields always follow the manifest; schedule and on/off only while no customer changed the row.
                await IdentityDb.ExecuteAsync(connection, tx, """
                    update job.definitions set module_id = $2, parameters = $3, concurrency_limit = $4, timeout_seconds = $5, retry_policy = $6, priority = $7,
                        schedule = case when updated_by = $9 then $8 else schedule end,
                        is_enabled = case when updated_by = $9 then $10 else is_enabled end,
                        row_version = row_version + 1, updated_at = $11
                    where code = $1 and source = 'product'
                      and (module_id, parameters, concurrency_limit, timeout_seconds, retry_policy, priority) is distinct from ($2, $3, $4, $5, $6, $7)
                       or (code = $1 and source = 'product' and updated_by = $9 and (schedule is distinct from $8 or is_enabled <> $10))
                    """, cancellationToken, spec.Code, spec.Module, new JsonPayload(spec.ParametersJson), spec.Concurrency, spec.TimeoutSeconds,
                    new JsonPayload(spec.RetryPolicyJson), spec.Priority, schedule, system, spec.EnabledByDefault, now).ConfigureAwait(false);
            }

            if (own)
            {
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return created;
        }
        finally
        {
            if (own)
            {
                await tx.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
