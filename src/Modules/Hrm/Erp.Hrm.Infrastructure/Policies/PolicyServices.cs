using Erp.Hrm.Application.Policies;
using Erp.Hrm.Contracts.Policies;
using Erp.Kernel.Contracts.Access;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Erp.Hrm.Infrastructure.Policies;

/// <summary>
/// Registers the policy framework of slice S1.23a. Both hosts get the HR module's policy-type manifest, the type catalogue (every
/// registered <see cref="PolicyTypeSource"/>, D-10) and the resolver (the hire, the transfer job and the reminders read probation through
/// it); the API host adds the policy services and the approval handlers of held activations. The kernel's <c>IOrgStructure</c> and
/// <c>ICustomFieldCatalog</c> are registered by each host.
/// </summary>
public static class PolicyServices
{
    /// <summary>The HR module's policy-type manifest (embedded <c>Access/policy-types.json</c>).</summary>
    public static string TypeManifest { get; } = Embedded("Erp.Hrm.Infrastructure.Access.policy-types.json");

    public static IServiceCollection AddHrmPolicyCore(this IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(PolicyTypeCatalog)))
        {
            return services;
        }

        services.AddSingleton(new PolicyTypeSource(TypeManifest));
        services.AddSingleton<IPolicyParameterCheck, Erp.Hrm.Application.Leave.LeavePolicyCheck>(); // S1.32a: what the leave schema cannot say
        services.AddSingleton<IPolicyParameterCheck, Erp.Hrm.Application.Attendance.AttendancePolicyCheck>(); // S1.29a: what the attendance schema cannot say
        services.AddSingleton<IPolicyParameterCheck, Erp.Hrm.Application.Attendance.SeasonalTimingCheck>(); // S1.28b: dates, month and times of a season
        services.AddSingleton<IPolicyParameterCheck, Erp.Hrm.Application.Payroll.LoanPolicyCheck>(); // S1.41: the figure each amount basis needs, a rate only with an interest method
        services.AddSingleton(sp => new PolicyTypeCatalog(sp.GetServices<PolicyTypeSource>(), sp.GetServices<IPolicyParameterCheck>()));
        services.AddScoped<PolicyResolver>();
        services.TryAddScoped<IPolicyResolver>(sp => sp.GetRequiredService<PolicyResolver>());
        return services;
    }

    public static IServiceCollection AddHrmPoliciesApi(this IServiceCollection services)
    {
        services.AddHrmPolicyCore();
        services.AddScoped<IPolicyService, PolicyService>();
        services.AddScoped<IHeldChangeHandler, PolicyVersionChangeHandler>();
        services.AddScoped<IHeldChangeHandler, PolicyAssignmentChangeHandler>();
        services.AddSingleton<Erp.Kernel.Contracts.Packs.IPackObjectHandler>(sp => new PolicyPackHandler(sp.GetRequiredService<PolicyTypeCatalog>())); // S1.23b
        services.AddScoped<Erp.Kernel.Contracts.Packs.IPackTakeGuard, PolicyPackTakeGuard>(); // S1.23b review finding 2
        return services;
    }

    private static string Embedded(string name)
    {
        using var stream = typeof(PolicyServices).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException(name + " is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
