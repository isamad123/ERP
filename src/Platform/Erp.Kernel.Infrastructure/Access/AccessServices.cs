using Erp.BuildingBlocks.Application.Security;
using Erp.Kernel.Contracts.Access;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Erp.Kernel.Infrastructure.Access;

/// <summary>Registers the access engine (slice S1.06) as the permission checker, data-scope filter and field access of a request.</summary>
public static class AccessServices
{
    public static IServiceCollection AddKernelAccess(this IServiceCollection services)
    {
        services.AddScoped<AccessEngine>();
        services.AddScoped<IPermissionChecker>(sp => sp.GetRequiredService<AccessEngine>());
        services.AddScoped<IDataScopeFilter>(sp => sp.GetRequiredService<AccessEngine>());
        services.AddScoped<IFieldAccess>(sp => sp.GetRequiredService<AccessEngine>());
        services.AddScoped<ICallerAccess>(sp => sp.GetRequiredService<AccessEngine>());
        services.TryAddScoped<IWorkforceDirectory, NoWorkforce>();
        return services;
    }
}

/// <summary>Used when the HR module is not installed: nobody has a team or a department, so those scopes match nothing.</summary>
internal sealed class NoWorkforce : IWorkforceDirectory
{
    public Task<WorkforcePosition> PositionOfUserAsync(Guid userId, DateOnly asOf, CancellationToken cancellationToken) => Task.FromResult(WorkforcePosition.None);

    public Task<IReadOnlyList<Guid>> DirectReportsAsync(IReadOnlyCollection<Guid> managerEmploymentIds, DateOnly asOf, CancellationToken cancellationToken) => Empty();

    public Task<IReadOnlyList<Guid>> InOrgUnitsAsync(IReadOnlyCollection<Guid> orgUnitIds, DateOnly asOf, CancellationToken cancellationToken) => Empty();

    public Task<IReadOnlyList<Guid>> InBranchesAsync(IReadOnlyCollection<Guid> branchIds, DateOnly asOf, CancellationToken cancellationToken) => Empty();

    public Task<IReadOnlyList<Guid>> InCompaniesAsync(IReadOnlyCollection<Guid> companyIds, CancellationToken cancellationToken) => Empty();

    public Task<IReadOnlyList<Guid>> PersonsOfAsync(IReadOnlyCollection<Guid> employmentIds, CancellationToken cancellationToken) => Empty();

    private static Task<IReadOnlyList<Guid>> Empty() => Task.FromResult<IReadOnlyList<Guid>>([]);
}
