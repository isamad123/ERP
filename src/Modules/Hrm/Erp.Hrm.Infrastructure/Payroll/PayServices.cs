using Erp.Hrm.Application.Payroll;
using Erp.Kernel.Contracts.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Hrm.Infrastructure.Payroll;

/// <summary>Registers the pay set-up services of slice S1.34a (called from the HR employment registration of each host).</summary>
public static class PayServices
{
    /// <summary>The readiness registry (embedded <c>Access/pay-readiness.json</c>).</summary>
    public static string ReadinessManifest { get; } = Embedded("Erp.Hrm.Infrastructure.Access.pay-readiness.json");

    /// <summary>Pay groups, the calendar and the readiness checklist for the API host.</summary>
    public static IServiceCollection AddHrmPayrollApi(this IServiceCollection services)
    {
        services.AddSingleton(new PayReadinessRegistry(ReadinessManifest));
        services.AddScoped<IPayGroupService, PayGroupService>();
        services.AddScoped<IPayCalendarService, PayCalendarService>();
        services.AddScoped<IPayReadinessCheck, BankDataCompleteCheck>();
        services.AddScoped<IPayReadinessCheck, StructuresAssignedCheck>();
        return services;
    }

    /// <summary>The daily cut-off reminder job of the worker host.</summary>
    public static IServiceCollection AddHrmPayrollWorker(this IServiceCollection services)
    {
        services.AddSingleton<IJobHandler, PayCutoffReminderJob>();
        return services;
    }

    private static string Embedded(string name)
    {
        using var stream = typeof(PayServices).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException(name + " is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
