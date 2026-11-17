using System.Text.Json;
using Erp.Kernel.Contracts.Workflow;
using Erp.Kernel.Domain.Workflow;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Workflow;

/// <summary>
/// Workflow set-up of a customer database, run by provisioning, by the migrator's <c>migrate</c> and when a licence adds
/// modules, after the numbering set-up (slice S1.10 build item 2; "seed default workflows" of FR-KRN-TEN-02): every
/// registered document type without a route gets one, keyed by the document type's code, with version 1 taken from the
/// module's default route and active at once, so documents can be sent for approval from the first day. A route that
/// exists is never touched, so a customer's edits are kept and a later release's default does not overwrite them.
/// Seeded rows are baseline data, like baseline roles and default numbering schemes, and are not change-logged.
/// </summary>
public static class WorkflowSetup
{
    /// <returns>The number of routes created.</returns>
    public static async Task<int> EnsureAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, IReadOnlyList<string> manifestFolders, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var catalog = new WorkflowCatalog(WorkflowManifest.FromFolders(manifestFolders));
        return await EnsureAsync(connection, transaction, catalog.All, now, cancellationToken).ConfigureAwait(false);
    }

    /// <returns>The number of routes created.</returns>
    public static async Task<int> EnsureAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, IEnumerable<WorkflowDocumentType> types, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(types);
        var own = transaction is null;
        var tx = transaction ?? await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var system = BaselineSeed.SystemUserId;
            await IdentityDb.ExecuteAsync(connection, tx, "select pg_advisory_xact_lock(hashtext('wf.workflow_setup'))", cancellationToken).ConfigureAwait(false);
            var created = 0;
            foreach (var type in types.OrderBy(t => t.Code, StringComparer.Ordinal))
            {
                if (await IdentityDb.ScalarAsync<bool>(connection, tx, "select exists (select 1 from wf.workflow_definitions where lower(code) = lower($1))", cancellationToken, type.Code).ConfigureAwait(false))
                {
                    continue;
                }

                var fields = type.Fields.ToDictionary(f => f.Name, f => new RouteField(f.Name, f.Type, f.Choices), StringComparer.Ordinal);
                var (route, problems) = RouteFormat.Parse(type.DefaultRoute, fields, type.BulkApproval);
                if (route is null)
                {
                    throw new InvalidDataException($"default route of {type.Code}: {string.Join("; ", problems)}");
                }

                var definitionId = Guid.CreateVersion7(now);
                var versionId = Guid.CreateVersion7(now);
                await IdentityDb.ExecuteAsync(connection, tx, """
                    insert into wf.workflow_definitions (id, code, name_i18n, document_type, status, is_active, requires_approval, created_at, created_by, updated_at, updated_by, row_version)
                    values ($1, $2, $3, $2, 'active', true, $4, $5, $6, $5, $6, 1)
                    """, cancellationToken, definitionId, type.Code, new JsonPayload(JsonSerializer.Serialize(type.Name)), type.RequiresApproval, now, system).ConfigureAwait(false);
                await IdentityDb.ExecuteAsync(connection, tx, """
                    insert into wf.workflow_versions (id, definition_id, version, definition, status, activated_at, activated_by, created_at, created_by, updated_at, updated_by, row_version)
                    values ($1, $2, 1, $3, 'active', $4, $5, $4, $5, $4, $5, 1)
                    """, cancellationToken, versionId, definitionId, new JsonPayload(RouteFormat.Serialize(route)), now, system).ConfigureAwait(false);
                await IdentityDb.ExecuteAsync(connection, tx, "update wf.workflow_definitions set active_version_id = $2 where id = $1", cancellationToken, definitionId, versionId).ConfigureAwait(false);
                created++;
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
