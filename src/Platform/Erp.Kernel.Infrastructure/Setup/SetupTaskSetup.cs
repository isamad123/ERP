using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Setup;

/// <summary>
/// Set-up task seeding of a customer database (slice S1.18a Build item 1, CR-074), run by provisioning, by the migrator's
/// <c>migrate</c> and when a licence adds modules, next to the job and notification set-up: every task of the installed modules'
/// manifests gets its row in <c>cfg.setup_tasks</c>, once for the customer (company null) and once per existing company, keyed by
/// module, task and company. A person's progress (status, note, who and when) is never overwritten; only <c>is_required</c> follows the
/// manifest. A company created later gets its rows when its checklist is first opened. Baseline data, not change-logged.
/// </summary>
public static class SetupTaskSetup
{
    /// <returns>The number of rows created or changed.</returns>
    public static Task<int> EnsureAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, IReadOnlyList<string> manifestFolders, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifestFolders);
        var modules = SetupManifest.FromFolders(manifestFolders).ToList();
        if (!modules.Exists(m => m.ModuleId == "kernel"))
        {
            modules.Insert(0, SetupManifest.Parse(SetupManifest.KernelJson));
        }

        return EnsureAsync(connection, transaction, modules, now, cancellationToken);
    }

    /// <returns>The number of rows created or changed.</returns>
    public static async Task<int> EnsureAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, IEnumerable<SetupModuleSpec> modules, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(modules);
        var list = modules.ToList();
        _ = new SetupCatalog(list); // refuses a wrong manifest before anything is written
        var own = transaction is null;
        var tx = transaction ?? await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await IdentityDb.ExecuteAsync(connection, tx, "select pg_advisory_xact_lock(hashtext('cfg.setup_task_setup'))", cancellationToken).ConfigureAwait(false);
            var companies = new List<Guid>();
            await using (var command = IdentityDb.Command(connection, tx, "select id from org.companies where not is_deleted order by id"))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    companies.Add(reader.GetGuid(0));
                }
            }

            var changed = 0;
            foreach (var module in list.OrderBy(m => m.ModuleId, StringComparer.Ordinal))
            {
                foreach (var task in module.Tasks.OrderBy(t => t.Key, StringComparer.Ordinal))
                {
                    var scopes = task.CompanyLevel ? companies.Select(c => (Guid?)c).ToList() : [null];
                    foreach (var company in scopes)
                    {
                        changed += await EnsureRowAsync(connection, tx, task, company, now, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            if (own)
            {
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return changed;
        }
        finally
        {
            if (own)
            {
                await tx.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Creates the rows of one company (called when its checklist is opened and by the service after a company is added).</summary>
    public static async Task<int> EnsureCompanyAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, IEnumerable<SetupModuleSpec> modules, Guid companyId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var changed = 0;
        foreach (var task in modules.SelectMany(m => m.Tasks))
        {
            changed += await EnsureRowAsync(connection, transaction, task, task.CompanyLevel ? companyId : null, now, cancellationToken).ConfigureAwait(false);
        }

        return changed;
    }

    private static Task<int> EnsureRowAsync(NpgsqlConnection connection, NpgsqlTransaction? tx, SetupTaskSpec task, Guid? company, DateTimeOffset now, CancellationToken cancellationToken) =>
        IdentityDb.ExecuteAsync(connection, tx, """
            insert into cfg.setup_tasks as t (id, module_id, task_key, company_id, is_required, status, created_at, created_by, updated_at, updated_by, row_version)
            values ($1, $2, $3, $4, $5, 'pending', $6, $7, $6, $7, 1)
            on conflict on constraint uq_setup_tasks_module_id_task_key_company_id do update
                set is_required = excluded.is_required, updated_at = excluded.updated_at, updated_by = excluded.updated_by, row_version = t.row_version + 1
                where t.is_required is distinct from excluded.is_required
            """, cancellationToken, Guid.CreateVersion7(now), task.ModuleId, task.Key, company, task.Required && task.Kind == "item", now, BaselineSeed.SystemUserId);
}
