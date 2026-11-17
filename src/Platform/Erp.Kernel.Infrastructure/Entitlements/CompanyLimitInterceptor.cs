using System.Runtime.CompilerServices;
using Erp.Kernel.Contracts.Entitlements;
using Erp.Kernel.Domain.Generated.Org;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Kernel.Infrastructure.Entitlements;

/// <summary>
/// The company limit (FR-KRN-IAM-18; slice S1.09 rulings Q7 and B5) on every EF write of org.companies, which is how the
/// generated company API creates and changes companies: a company saved as active and not deleted, that was not before
/// (created, made active again or restored), takes a seat through the kernel limit guard inside the save's transaction.
/// At the limit the save is refused with <see cref="LimitReachedException"/> and nothing is written.
/// </summary>
/// <remarks>Registered after the audit interceptor, which already opened the save's transaction for this audited table; if
/// none is open the interceptor opens one and commits it with the save.</remarks>
public sealed class CompanyLimitInterceptor : SaveChangesInterceptor
{
    private readonly ConditionalWeakTable<DbContext, IDbContextTransaction> _owned = [];

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is not { } context)
        {
            return result;
        }

        var activated = context.ChangeTracker.Entries<Company>().Where(BecomesActive).Select(e => e.Entity.Id).ToList();
        if (activated.Count == 0)
        {
            return result;
        }

        var transaction = context.Database.CurrentTransaction;
        if (transaction is null)
        {
            transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            _owned.AddOrUpdate(context, transaction);
        }

        var services = context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider
            ?? throw new InvalidOperationException("The context has no application service provider (register it with AddDbContext).");
        var guard = services.GetRequiredService<ILimitGuard>();
        try
        {
            foreach (var id in activated)
            {
                await guard.TakeAsync(LimitKeys.Companies, transaction.GetDbTransaction(), id, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await EndAsync(context, commit: false).ConfigureAwait(false);
            throw;
        }

        return result;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        return eventData.Context is { } context && context.ChangeTracker.Entries<Company>().Any(BecomesActive)
            ? throw new NotSupportedException("Company writes save asynchronously (SaveChangesAsync), so the limit guard joins the transaction.")
            : result;
    }

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is { } context)
        {
            await EndAsync(context, commit: true).ConfigureAwait(false);
        }

        return result;
    }

    public override async Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is { } context)
        {
            await EndAsync(context, commit: false).ConfigureAwait(false);
        }
    }

    private async Task EndAsync(DbContext context, bool commit)
    {
        if (_owned.TryGetValue(context, out var owned))
        {
            _owned.Remove(context);
            if (commit)
            {
                await owned.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await owned.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }

            await owned.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Ruling B5: counted companies are active and not deleted; "reactivate" is inactive → active or a restore.</summary>
    private static bool BecomesActive(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<Company> entry)
    {
        var activeNow = entry.Entity.Status == "active" && !entry.Entity.IsDeleted;
        return entry.State switch
        {
            EntityState.Added => activeNow,
            EntityState.Modified => activeNow && (entry.OriginalValues.GetValue<string>(nameof(Company.Status)) != "active" || entry.OriginalValues.GetValue<bool>(nameof(Company.IsDeleted))),
            _ => false,
        };
    }
}
