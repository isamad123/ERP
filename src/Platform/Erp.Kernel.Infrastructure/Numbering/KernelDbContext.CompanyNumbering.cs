using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Domain.Generated.Org;
using Erp.Kernel.Infrastructure.Numbering;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Erp.Kernel.Infrastructure.Generated;

/// <summary>
/// A company created through the generated API gets its default numbering schemes in the same transaction (slice S1.14a
/// Build item 2, ruling Q2, CR-070): without them its documents could not be numbered until the next <c>migrate</c>. The
/// company limit (FR-KRN-IAM-18) is checked by <see cref="Entitlements.CompanyLimitInterceptor"/> inside the same save; the
/// first branch comes from the database trigger (SC-2). A host without the document-type registry (the worker) leaves the
/// schemes to the next <c>migrate</c>, as before.
/// </summary>
public sealed partial class KernelDbContext
{
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        RefuseAccessSettings();
        var created = ChangeTracker.Entries<Company>().Where(e => e.State == EntityState.Added && !e.Entity.IsDeleted).Select(e => e.Entity.Id).ToList();
        if (created.Count == 0)
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken).ConfigureAwait(false);
        }

        // One transaction for the company row, its audit entries, the limit seat and the schemes; the interceptors join it.
        var owned = Database.CurrentTransaction is null ? await Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false) : null;
        try
        {
            var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken).ConfigureAwait(false);
            await SeedNumberingAsync(created, cancellationToken).ConfigureAwait(false);
            if (owned is not null)
            {
                await owned.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
        catch
        {
            if (owned is not null)
            {
                await owned.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            if (owned is not null)
            {
                await owned.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task SeedNumberingAsync(IReadOnlyList<Guid> companies, CancellationToken cancellationToken)
    {
        var services = this.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider;
        if (services?.GetService<DocumentTypeRegistry>() is not { } registry || services.GetService<IClock>() is not { } clock
            || Database.CurrentTransaction?.GetDbTransaction() is not NpgsqlTransaction transaction)
        {
            return;
        }

        var connection = (NpgsqlConnection)Database.GetDbConnection();
        var now = clock.UtcNow;
        foreach (var company in companies)
        {
            foreach (var type in registry.All.Where(t => !t.TenantWide))
            {
                await NumberingDb.EnsureDefaultAsync(connection, transaction, type, company, now, audited: true, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
