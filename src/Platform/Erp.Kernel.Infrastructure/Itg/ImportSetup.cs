using System.Text.Json.Nodes;
using Erp.Kernel.Application.Itg;
using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Itg;

/// <summary>
/// Import set-up of a customer database (slice S1.16a, FR-ADM-INT-04), run by provisioning, by <c>migrate</c> and when a licence
/// adds modules: every import type of the installed modules gets its shipped template (<c>default.&lt;type&gt;</c>, version 1), keyed
/// by code. A template a customer changed (version above 1 or edited by someone else) is never overwritten.
/// </summary>
public static class ImportSetup
{
    public const string DefaultPrefix = "default.";

    /// <returns>The number of templates created.</returns>
    public static Task<int> EnsureAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, IReadOnlyList<string> manifestFolders, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifestFolders);
        var types = ImportManifest.Parse(ImportManifest.KernelJson).Concat(ImportManifest.FromFolders(manifestFolders)).GroupBy(t => t.Code, StringComparer.Ordinal).Select(g => g.Last()).ToList();
        return EnsureAsync(connection, transaction, types, now, cancellationToken);
    }

    /// <returns>The number of templates created.</returns>
    public static async Task<int> EnsureAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, IEnumerable<ImportTypeDefinition> types, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(types);
        var own = transaction is null;
        var tx = transaction ?? await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await IdentityDb.ExecuteAsync(connection, tx, "select pg_advisory_xact_lock(hashtext('itg.import_setup'))", cancellationToken).ConfigureAwait(false);
            var created = 0;
            foreach (var type in types.OrderBy(t => t.Code, StringComparer.Ordinal))
            {
                var name = new JsonObject();
                foreach (var (language, text) in type.Name)
                {
                    name[language] = text + (language == "ur" ? " (پہلے سے طے شدہ سانچہ)" : " (shipped template)");
                }

                created += await IdentityDb.ExecuteAsync(connection, tx, """
                    insert into itg.import_templates (id, code, name_i18n, entity_type, format, mapping, version, mode, is_default, is_active,
                        created_at, created_by, updated_at, updated_by, row_version)
                    values ($1, $2, $3, $4, 'csv', $5, 1, 'create', true, true, $6, $7, $6, $7, 1)
                    on conflict (lower(code)) do nothing
                    """, cancellationToken, Guid.CreateVersion7(now), DefaultPrefix + type.Code, new JsonPayload(name.ToJsonString()), type.Code,
                    new JsonPayload(TemplateMapping.DefaultFor(type).ToJsonString()), now, BaselineSeed.SystemUserId).ConfigureAwait(false);
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
