using Erp.Kernel.Infrastructure.ControlPlane.Provisioning;
using Erp.Kernel.Infrastructure.Identity;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Notifications;

/// <summary>
/// Notification set-up of a customer database (slice S1.11 Build item 9, ruling Q15), run by provisioning, by the
/// migrator's <c>migrate</c> and when a licence adds modules, next to the job set-up: the product's default texts of every
/// catalogue event become version 1 of <c>ntf.templates</c> (source <c>product</c>), keyed by event, channel and language;
/// each event has an in-app and an e-mail text, an identity e-mail only the e-mail text (slice S1.11b2).
/// A later product text replaces version 1 in place; a customer's edit is a higher tenant version, so it is never
/// overwritten and "back to product text" only retires the customer versions. Baseline data, not change-logged.
/// </summary>
public static class NotificationSetup
{
    /// <returns>The number of product texts created or changed.</returns>
    public static Task<int> EnsureAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, IReadOnlyList<string> manifestFolders, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifestFolders);
        var specs = NotificationCatalog.FromFolders(manifestFolders).ToList();
        if (!specs.Exists(s => s.Module == "kernel"))
        {
            specs = [.. NotificationCatalog.Parse(NotificationCatalog.KernelJson), .. specs];
        }

        return EnsureAsync(connection, transaction, specs, now, cancellationToken);
    }

    /// <returns>The number of product texts created or changed.</returns>
    public static async Task<int> EnsureAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, IEnumerable<NotificationEventSpec> specs, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(specs);
        var list = specs.ToList();
        _ = new NotificationCatalog(list); // refuses a wrong catalogue before anything is written
        var own = transaction is null;
        var tx = transaction ?? await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await IdentityDb.ExecuteAsync(connection, tx, "select pg_advisory_xact_lock(hashtext('ntf.notification_setup'))", cancellationToken).ConfigureAwait(false);
            var system = BaselineSeed.SystemUserId;
            var changed = 0;
            foreach (var spec in list.OrderBy(s => s.Code, StringComparer.Ordinal))
            {
                foreach (var (language, text) in spec.Texts.OrderBy(t => t.Key, StringComparer.Ordinal))
                {
                    // In-app text and e-mail text (slice S1.11b2, P5); a direct (identity) entry has the e-mail only.
                    var channels = spec.EmailOnly
                        ? new[] { ("email", text.MailSubject, text.MailBody) }
                        : [("in_app", text.Title, text.Body), ("email", text.MailSubject, text.MailBody)];
                    foreach (var (channel, subject, body) in channels)
                    {
                        changed += await IdentityDb.ExecuteAsync(connection, tx, """
                            insert into ntf.templates as t (id, event_code, channel, language, subject, body, version, source, status, created_at, created_by, updated_at, updated_by, row_version)
                            values ($1, $2, $8, $3, $4, $5, 1, 'product', 'active', $6, $7, $6, $7, 1)
                            on conflict (event_code, channel, language, version) do update
                                set subject = excluded.subject, body = excluded.body, updated_at = excluded.updated_at, updated_by = excluded.updated_by, row_version = t.row_version + 1
                                where t.source = 'product' and (t.subject, t.body) is distinct from (excluded.subject, excluded.body)
                            """, cancellationToken, Guid.CreateVersion7(now), spec.Code, language, subject, body, now, system, channel).ConfigureAwait(false);
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
}
