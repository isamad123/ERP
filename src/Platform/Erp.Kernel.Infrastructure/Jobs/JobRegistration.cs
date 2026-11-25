using Erp.BuildingBlocks.Application.Audit;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Kernel.Application.Jobs;
using Erp.Kernel.Contracts.Entitlements;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Infrastructure.ControlPlane;
using Erp.Kernel.Infrastructure.ControlPlane.Entitlements;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Erp.Kernel.Infrastructure.Jobs;

/// <summary>Service registration of the jobs framework (slice S1.11a).</summary>
public static class JobRegistration
{
    /// <summary>
    /// The job centre of the API host (Build item 7): it reads and queues runs but runs nothing, so the catalogue needs no
    /// handlers. Modules register their <see cref="JobManifestSource"/> in both hosts.
    /// </summary>
    public static IServiceCollection AddKernelJobCentre(this IServiceCollection services)
    {
        services.AddSingleton(sp => JobCatalog.WithoutHandlers(sp.GetServices<JobManifestSource>()));
        services.TryAddScoped<JobRunContext>();
        services.AddScoped<IJobCentre, JobCentreService>();
        services.TryAddScoped<IJobRequests, JobRequests>(); // S1.35b: runs a module starts from its own service
        return services;
    }

    /// <summary>
    /// The scheduler, runner and event dispatcher of the worker host (Build items 2 to 6), with the kernel's jobs. A job
    /// run's scope acts as the job actor or the requester (<see cref="JobRunContext"/>, ruling Q4): the host's
    /// <c>ICurrentActor</c> and <c>IAuditContext</c> are the job's. The host adds tenancy, audit, access, settings,
    /// licence resolution and the workflow engine.
    /// </summary>
    public static IServiceCollection AddKernelJobWorker(this IServiceCollection services, JobWorkerOptions worker, EventDispatcherOptions events, KernelJobOptions kernel)
    {
        services.AddSingleton(worker);
        services.AddSingleton(events);
        services.AddSingleton(kernel);
        services.TryAddScoped<JobRunContext>();
        services.AddScoped<ICurrentActor, JobCurrentActor>();
        services.AddScoped<IAuditContext, JobAuditContext>();
        // The HR module's active employee counter, when the host registers it (S1.20b; S1.09b ruling Q8: the monthly meter).
        services.AddSingleton(sp => new LicenceMaintenance(sp.GetRequiredService<ControlPlaneOptions>(), sp.GetRequiredService<Erp.BuildingBlocks.Application.Tenancy.ISecretStore>(), sp.GetRequiredService<IClock>(), sp.GetService<IActiveEmployeeCounter>()));
        services.AddKernelJobHandlers();
        services.TryAddScoped<IJobRequests, JobRequests>(); // S1.35b: a running job may queue or cancel runs (the payroll calculation's lanes)
        services.AddSingleton(sp => new JobCatalog(sp.GetServices<JobManifestSource>(), sp.GetServices<IJobHandler>()));
        services.AddSingleton<JobWorker>();
        services.AddSingleton<EventDispatcher>();
        return services;
    }
}
