using Erp.Kernel.Contracts.Setup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Erp.Kernel.Infrastructure.Setup;

/// <summary>Service registration of the readiness checklist (slice S1.18a). Modules register their <see cref="SetupManifestSource"/> and <see cref="IReadinessCheck"/>s.</summary>
public static class SetupRegistration
{
    public static IServiceCollection AddSetupReadiness(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(sp => new SetupCatalog(sp.GetServices<SetupManifestSource>()));
        services.AddScoped<IReadinessCheck, CompanyProfileCheck>();
        services.AddScoped<IReadinessCheck, LocationCheck>();
        services.AddScoped<IReadinessCheck, UnitStructureCheck>();
        services.AddScoped<IReadinessCheck, HolidayCalendarCheck>();
        services.AddScoped<IReadinessCheck, NumberingCheck>();
        services.AddScoped<IReadinessCheck, ApprovalRoutesCheck>();
        services.AddScoped<IReadinessCheck, OtherAdministratorCheck>();
        services.AddScoped<IReadinessService, ReadinessService>();
        services.AddScoped<IGoLiveGate, GoLiveGate>();
        return services;
    }
}
