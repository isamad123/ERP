using Erp.BuildingBlocks.Application.Data;
using Erp.Kernel.Contracts.Org;
using Erp.Kernel.Domain.Generated.Org;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Kernel.Infrastructure.Org;

/// <summary>
/// A unit is switched off only when no module still uses it (slice S1.22b Build item 9, FR-HRM-POS-08, ruling Q6): on every EF save that
/// makes an active unit inactive or deletes it (which is how the generated unit API does both), each <see cref="IOrgUnitUseGuard"/> is
/// asked; the first refusal stops the save with a 409 carrying the guard's localization key. Nothing is written when refused.
/// </summary>
public sealed class OrgUnitUseInterceptor : SaveChangesInterceptor
{
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is not { } context)
        {
            return result;
        }

        var switchedOff = context.ChangeTracker.Entries<OrgUnit>().Where(SwitchedOff).Select(e => e.Entity.Id).ToList();
        if (switchedOff.Count == 0)
        {
            return result;
        }

        var services = context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider
            ?? throw new InvalidOperationException("The context has no application service provider (register it with AddDbContext).");
        foreach (var guard in services.GetServices<IOrgUnitUseGuard>())
        {
            foreach (var id in switchedOff)
            {
                if (await guard.DeactivationProblemAsync(id, cancellationToken).ConfigureAwait(false) is { } problem)
                {
                    throw new DataConflictException(problem, null);
                }
            }
        }

        return result;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        return eventData.Context is { } context && context.ChangeTracker.Entries<OrgUnit>().Any(SwitchedOff)
            ? throw new NotSupportedException("Unit writes save asynchronously (SaveChangesAsync), so the use guards can be asked.")
            : result;
    }

    private static bool SwitchedOff(EntityEntry<OrgUnit> entry) =>
        entry.State == EntityState.Modified
        && ((entry.OriginalValues.GetValue<bool>(nameof(OrgUnit.IsActive)) && !entry.Entity.IsActive)
            || (!entry.OriginalValues.GetValue<bool>(nameof(OrgUnit.IsDeleted)) && entry.Entity.IsDeleted));
}
