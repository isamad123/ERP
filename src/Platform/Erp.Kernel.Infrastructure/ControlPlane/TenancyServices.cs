using Erp.BuildingBlocks.Application.Tenancy;
using Erp.Kernel.Application.ControlPlane.Tenancy;
using Erp.Kernel.Infrastructure.ControlPlane.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Kernel.Infrastructure.ControlPlane;

/// <summary>Registers tenant resolution and per-customer connections (S1.03). Module DbContexts are registered by the host on <see cref="ITenantConnectionProvider"/>.</summary>
public static class TenancyServices
{
    public static IServiceCollection AddErpTenancy(this IServiceCollection services, ControlPlaneOptions options, ISecretStore secrets)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(options);
        services.AddSingleton(secrets);
        services.AddSingleton<TenantDirectory>();
        services.AddSingleton<ITenantDirectory>(sp => sp.GetRequiredService<TenantDirectory>());
        services.AddSingleton<TenantDataSources>();
        services.AddSingleton<ITenantDataSourceFactory>(sp => sp.GetRequiredService<TenantDataSources>());
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<ITenantConnectionProvider>(sp => sp.GetRequiredService<TenantContext>());
        return services;
    }
}
