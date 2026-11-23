using Erp.BuildingBlocks.Application.Audit;
using Erp.BuildingBlocks.Application.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Kernel.Infrastructure.Audit;

/// <summary>Registers the audit services of slice S1.07 (the host adds its <see cref="IAuditContext"/>).</summary>
public static class AuditRegistration
{
    /// <summary>The change-log writer, tamper recorder, access log, subject keys and the audit queries.</summary>
    public static IServiceCollection AddKernelAudit(this IServiceCollection services, IEnumerable<AuditedTable> tables)
    {
        services.AddSingleton(new AuditCatalog(tables));
        services.AddSingleton<ChangeAuditInterceptor>();
        services.AddSingleton<AuditTamperInterceptor>();
        services.AddScoped<ISensitiveAccessLog, SensitiveAccessLog>();
        services.AddScoped<SubjectKeyService>();
        services.AddScoped<Contracts.Audit.IAuditEvents, AuditEventWriter>(); // modules' business events (S1.20a)
        return services;
    }

    /// <summary>Every save of the context writes its change-log entries in the same transaction (build item 1).</summary>
    public static DbContextOptionsBuilder UseErpAudit(this DbContextOptionsBuilder options, IServiceProvider services) =>
        options.AddInterceptors(services.GetRequiredService<ChangeAuditInterceptor>(), services.GetRequiredService<AuditTamperInterceptor>());
}
