using Erp.Hrm.Application.People;
using Erp.Kernel.Contracts.Access;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Hrm.Infrastructure.People;

/// <summary>Registers the person services (slice S1.20a) and the handler of held merges.</summary>
public static class PeopleServices
{
    public static IServiceCollection AddHrmPeople(this IServiceCollection services)
    {
        services.AddScoped<PersonMerger>();
        services.AddScoped<PersonService>();
        services.AddScoped<IPersonService>(sp => sp.GetRequiredService<PersonService>());
        services.AddScoped<IHeldChangeHandler, PersonMergeHandler>();
        return services;
    }
}
