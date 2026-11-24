using Erp.Kernel.Application.Itg;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Erp.Kernel.Infrastructure.Itg;

/// <summary>Service registration of the import engine (slice S1.16a): the catalogue of import types, the file keeper, the service and the hand-written writers. Modules add their <c>ImportManifestSource</c> in both hosts.</summary>
public static class ImportRegistration
{
    public static IServiceCollection AddKernelImportEngine(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(sp => new ImportCatalog(sp.GetServices<Erp.Kernel.Contracts.Itg.ImportManifestSource>()));
        // Generated writers check custom field values on every write; the worker host runs them too (S1.15b rule, S1.16a).
        services.TryAddScoped<Erp.Kernel.Contracts.Metadata.ICustomFieldValidator, Erp.Kernel.Infrastructure.Metadata.CustomFieldValidator>();
        services.TryAddScoped<ImportFiles>();
        services.TryAddScoped<IImportService, ImportService>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IImportRowWriter, UserImportWriter>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IImportRowWriter, OrgUnitImportWriter>());
        return services;
    }
}
