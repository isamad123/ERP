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
        services.AddSingleton(sp => new PolicyTypeCatalog(sp.GetServices<PolicyTypeSource>()));
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
        return services;
    }

    private static string Embedded(string name)
    {
        using var stream = typeof(PolicyServices).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException(name + " is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
