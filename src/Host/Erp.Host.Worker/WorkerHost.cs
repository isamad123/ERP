using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Application.Tenancy;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Time;
using Erp.Hrm.Infrastructure.Access;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Kernel.Application.ControlPlane.Tenancy;
using Erp.Kernel.Contracts.Entitlements;
using Erp.Kernel.Contracts.Settings;
using Erp.Kernel.Infrastructure.Access;
using Erp.Kernel.Infrastructure.Audit;
using Erp.Kernel.Infrastructure.ControlPlane;
using Erp.Kernel.Infrastructure.ControlPlane.Secrets;
using Erp.Kernel.Infrastructure.Entitlements;
using Erp.Kernel.Infrastructure.Generated;
using Erp.Kernel.Infrastructure.Identity;
using Erp.Kernel.Infrastructure.Jobs;
using Erp.Kernel.Infrastructure.Notifications;
using Erp.Kernel.Infrastructure.Settings;
using Erp.Kernel.Infrastructure.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Host.Worker;

/// <summary>
/// Composition of the worker host (slice S1.11a): the job scheduler and runner and the event dispatcher, with what job runs
/// need (tenancy, audit, the customer DbContexts and generated repositories, the access engine, settings, licence and
/// the workflow engine). Used by Program and by the integration tests, so tests run jobs exactly as the worker does.
/// <para>Settings (environment variables, as the migrator's; docs/runbooks/tenant-operations.md): <c>ERP_CONTROL_PLANE</c>,
/// <c>ERP_SECRETS_FILE</c>, <c>ERP_SECRETS_KEY</c>, <c>ERP_PLATFORM_ACTOR</c> (ruling P6); <c>Jobs:Slots</c> (4),
/// <c>Jobs:PerTenantSlots</c> (2), <c>Jobs:LeaseSeconds</c> (60), <c>Jobs:PollMilliseconds</c> (2000),
/// <c>Events:PollMilliseconds</c> (2000), <c>Events:BatchSize</c> (100), <c>Settings:RefreshMilliseconds</c> (1000).</para>
/// </summary>
public static class WorkerHost
{
    /// <summary>The poll intervals of the two loops (rulings Q5, Q7).</summary>
    public sealed record LoopOptions(TimeSpan JobPoll, TimeSpan EventPoll);

    public static IServiceCollection Configure(IServiceCollection services, IConfiguration config, ISecretStore? secrets = null, Action<IServiceCollection>? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);
        var controlPlane = config["ERP_CONTROL_PLANE"] ?? config.GetConnectionString("ControlPlane")
            ?? throw new InvalidOperationException("ERP_CONTROL_PLANE is not set (docs/runbooks/tenant-operations.md).");
        var options = new ControlPlaneOptions { ConnectionString = controlPlane };
        var secretStore = secrets ?? EncryptedFileSecretStore.FromSettings(config["ERP_SECRETS_FILE"], config["ERP_SECRETS_KEY"]);

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(TimeProvider.System);
        services.AddErpTenancy(options, secretStore);
        services.AddKernelAudit([.. KernelAuditCatalog.All, .. HrmAuditCatalog.All]);
        services.AddDbContext<KernelDbContext>((sp, o) => o.UseNpgsql(sp.GetRequiredService<ITenantConnectionProvider>().DataSource).UseErpAudit(sp)
            .AddInterceptors(sp.GetRequiredService<CompanyLimitInterceptor>()));
        services.AddDbContext<HrmDbContext>((sp, o) => o.UseNpgsql(sp.GetRequiredService<ITenantConnectionProvider>().DataSource).UseErpAudit(sp));
        services.AddKernelGeneratedRepositories().AddHrmGeneratedRepositories();

        // Field encryption of the customer (S1.05 P7): generated repositories encrypt enc columns on write.
        services.AddSingleton<FieldKeyCache>();
        services.AddScoped<IFieldEncryptorProvider, TenantFieldEncryptorProvider>();
        services.AddKernelAccess();
        services.AddHrmAccess();
        services.AddSingleton(new ResourceCatalog([.. KernelResourceCatalog.All, .. HrmResourceCatalog.All]));
        services.AddScoped<AccessAdministrationService>();

        services.AddSingleton(new SettingsCacheOptions(TimeSpan.FromMilliseconds(config.GetValue("Settings:RefreshMilliseconds", 1000))));
        services.AddSingleton<SettingsCache>();
        services.AddSingleton<ILicenceResolver, LicenceResolver>();
        services.AddScoped<ILicensedFeatures, EntitlementFeatures>();
        services.AddSingleton<ILimitCounter, NamedUserCounter>();
        services.AddSingleton<ILimitCounter, ConcurrentUserCounter>();
        services.AddSingleton<ILimitCounter, CompanyCounter>();
        services.AddScoped<LimitWarnings>();
        services.AddScoped<ILimitWarningSource>(sp => sp.GetRequiredService<LimitWarnings>());
        services.AddScoped<ILimitGuard, LimitGuard>();
        services.AddSingleton<CompanyLimitInterceptor>();
        services.AddScoped<SettingsService>();
        services.AddScoped<ISettings>(sp => sp.GetRequiredService<SettingsService>());
        services.AddScoped<IFeatureFlags>(sp => sp.GetRequiredService<SettingsService>());

        // Workflow (S1.10b): modules register their document types and, for HR, the approver directory here as in the API host.
        services.AddKernelWorkflow();

        // Notifications (S1.11b1): the consumer that turns events into in-app items; modules register their catalogue sources here.
        services.AddKernelNotificationConsumer();

        // Jobs and events (S1.11a, rulings Q5, Q7, P6).
        var lease = TimeSpan.FromSeconds(config.GetValue("Jobs:LeaseSeconds", 60));
        services.AddKernelJobWorker(
            new JobWorkerOptions(JobWorkerOptions.NewWorkerId(), config.GetValue("Jobs:Slots", 4), config.GetValue("Jobs:PerTenantSlots", 2), lease),
            new EventDispatcherOptions(config.GetValue("Events:BatchSize", 100)),
            new KernelJobOptions(config["ERP_PLATFORM_ACTOR"]));
        services.AddSingleton(new LoopOptions(
            TimeSpan.FromMilliseconds(config.GetValue("Jobs:PollMilliseconds", 2000)),
            TimeSpan.FromMilliseconds(config.GetValue("Events:PollMilliseconds", 2000))));
        overrides?.Invoke(services);
        return services;
    }
}
