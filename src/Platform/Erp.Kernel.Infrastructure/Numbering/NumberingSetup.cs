using Erp.Kernel.Infrastructure.ControlPlane.Platform;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Numbering;

/// <summary>
/// Numbering set-up of a customer database, run by provisioning and by the migrator's <c>migrate</c> after the access
/// set-up (slice S1.08 ruling Q7, part of FR-KRN-TEN-02): registers the numbering settings and gives every company a
/// default scheme for every registered document type it has none of (tenant-wide types once for the customer).
/// Idempotent, keyed by company and document type, so document types of later releases appear automatically; a
/// scheme an administrator retired is never brought back. Seeded schemes are baseline data (like baseline roles) and are
/// not change-logged; schemes created later, by people or on first use, are.
/// </summary>
public static class NumberingSetup
{
    /// <returns>The number of default schemes created.</returns>
    public static async Task<int> EnsureAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, IReadOnlyList<string> manifestFolders, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var registry = DocumentTypeRegistry.FromManifestFolders(manifestFolders);
        var own = transaction is null;
        var tx = transaction ?? await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Sql.ExecuteAsync(connection, tx, "select pg_advisory_xact_lock(hashtext('cfg.numbering_setup'))", cancellationToken).ConfigureAwait(false);
            await NumberingSettings.SeedAsync(connection, tx, cancellationToken).ConfigureAwait(false);
            var companies = new List<Guid>();
            await using (var command = Sql.Command(connection, tx, "select id from org.companies where not is_deleted order by id", []))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    companies.Add(reader.GetGuid(0));
                }
            }

            var created = 0;
            foreach (var type in registry.All)
            {
                foreach (var company in type.TenantWide ? [null] : companies.Select(c => (Guid?)c))
                {
                    created += await NumberingDb.EnsureDefaultAsync(connection, tx, type, company, now, audited: false, cancellationToken).ConfigureAwait(false) ? 1 : 0;
                }
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
