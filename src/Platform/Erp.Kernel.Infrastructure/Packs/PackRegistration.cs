using Erp.Kernel.Contracts.Access;
using Erp.Kernel.Contracts.Packs;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Kernel.Infrastructure.Packs;

/// <summary>
/// Registers the pack manager of the API host (slice S1.23b): the kernel's object handlers, the installer over every registered
/// <see cref="IPackObjectHandler"/> (modules add theirs, D-10), the manager and the approval handler of held pack actions.
/// </summary>
public static class PackRegistration
{
    public static IServiceCollection AddPackManager(this IServiceCollection services)
    {
        foreach (var handler in KernelPackHandlers.All)
        {
            services.AddSingleton(handler);
        }

        services.AddScoped(sp => new PackInstaller(sp.GetServices<IPackObjectHandler>()));
        services.AddScoped<PackManager>();
        services.AddScoped<IPackManager>(sp => sp.GetRequiredService<PackManager>());
        services.AddScoped<IHeldChangeHandler, PackChangeHandler>();
        return services;
    }
}
