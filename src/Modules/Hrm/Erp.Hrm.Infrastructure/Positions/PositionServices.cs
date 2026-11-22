using Erp.Hrm.Application.Positions;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Hrm.Infrastructure.Positions;

/// <summary>
/// Registers the position services of slice S1.22a. Both hosts get the outcome handler of the document type
/// <c>hrm.strength_exception</c> (an approval can be decided through either); the API host adds the services behind the endpoints.
/// The document type itself is in the HR workflow manifest (<c>Access/workflow.json</c>), registered with the employment services.
/// </summary>
public static class PositionServices
{
    public static IServiceCollection AddHrmPositions(this IServiceCollection services)
    {
        services.AddScoped<Kernel.Contracts.Workflow.IWorkflowOutcomeHandler, StrengthExceptionHandler>();
        services.AddScoped<Kernel.Contracts.Settings.ISettingChangeGuard, OrgModelChangeGuard>();
        return services;
    }

    public static IServiceCollection AddHrmPositionsApi(this IServiceCollection services)
    {
        services.AddScoped<IPositionService, PositionService>();
        services.AddScoped<IStrengthExceptionService, StrengthExceptionService>();
        return services;
    }
}
