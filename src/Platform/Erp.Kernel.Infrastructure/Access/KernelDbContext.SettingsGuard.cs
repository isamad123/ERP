using Erp.BuildingBlocks.Application.Security;
using Erp.Kernel.Domain.Generated.Cfg;
using Microsoft.EntityFrameworkCore;

namespace Erp.Kernel.Infrastructure.Generated;

/// <summary>
/// The identity and access settings (keys <c>iam.*</c>) change only through the access settings service, which records
/// each change and can require a second administrator (review S1.06 finding 5, CR-025). The generated settings API
/// (EF Core, this context) is refused for those keys with 403 <c>errors.settings.use_access_service</c>; the service
/// writes with SQL in its own transaction.
/// </summary>
public sealed partial class KernelDbContext
{
    public const string UseAccessService = "errors.settings.use_access_service";

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        RefuseAccessSettings();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        RefuseAccessSettings();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void RefuseAccessSettings()
    {
        foreach (var entry in ChangeTracker.Entries<SettingValue>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                && (IsAccessKey(entry.Entity.SettingKey) || (entry.State != EntityState.Added && IsAccessKey(entry.OriginalValues.GetValue<string>(nameof(SettingValue.SettingKey))))))
            {
                throw new AccessDeniedException(UseAccessService);
            }
        }
    }

    private static bool IsAccessKey(string? key) => key is not null && key.StartsWith("iam.", StringComparison.Ordinal);
}
