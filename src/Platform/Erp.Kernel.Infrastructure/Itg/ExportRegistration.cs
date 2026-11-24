using Erp.Kernel.Application.Itg;
using Erp.Kernel.Contracts.Access;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Erp.Kernel.Infrastructure.Itg;

/// <summary>Service registration of the export engine (slice S1.16b): the file keeper, the service and the approval handler. Each module adds its generated readers in both hosts.</summary>
public static class ExportRegistration
{
    public static IServiceCollection AddKernelExportEngine(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<ExportFiles>();
        services.TryAddScoped<IExportService, ExportService>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IHeldChangeHandler, ExportChangeHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<Erp.Kernel.Contracts.Itg.IExportRowReader, AuditExportReader>());
        return services;
    }
}
