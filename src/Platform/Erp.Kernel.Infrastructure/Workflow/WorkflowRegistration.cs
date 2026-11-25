using Erp.Kernel.Application.Workflow;
using Erp.Kernel.Contracts.Workflow;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Erp.Kernel.Infrastructure.Workflow;

/// <summary>Service registration of the workflow engine (slice S1.10).</summary>
public static class WorkflowRegistration
{
    /// <summary>
    /// The engine, route administration and the document-type catalogue. Modules add their
    /// <see cref="IWorkflowDocumentTypeSource"/>, <see cref="IWorkflowOutcomeHandler"/>s and, for HR, the
    /// <see cref="IApproverDirectory"/>; without a directory, manager, department-head and position steps cannot be activated.
    /// </summary>
    public static IServiceCollection AddKernelWorkflow(this IServiceCollection services)
    {
        services.AddSingleton(sp => new WorkflowCatalog(sp.GetServices<IWorkflowDocumentTypeSource>()));
        services.TryAddScoped<IApproverDirectory, NoApproverDirectory>();
        services.AddScoped<WorkflowEngine>();
        services.AddScoped<IWorkflowService>(sp => sp.GetRequiredService<WorkflowEngine>());
        services.AddScoped<IWorkflowInbox>(sp => sp.GetRequiredService<WorkflowEngine>());
        services.AddScoped<IWorkflowMonitor>(sp => sp.GetRequiredService<WorkflowEngine>());
        services.AddScoped<IWorkflowQueries>(sp => sp.GetRequiredService<WorkflowEngine>());
        services.AddScoped<DelegationService>();
        services.AddScoped<IDelegationService>(sp => sp.GetRequiredService<DelegationService>());
        services.AddScoped<WorkflowAdministrationService>();
        services.AddScoped<IWorkflowAdministration>(sp => sp.GetRequiredService<WorkflowAdministrationService>());
        return services;
    }

    /// <summary>
    /// Checks the workflow set-up when a host starts (review S1.10a finding 4): the document-type manifests are read and
    /// checked now (a wrong manifest stops the host), and every registered document type has exactly one outcome handler,
    /// so a module that forgot its handler cannot run requests that fail only at their final decision.
    /// </summary>
    public static void VerifyWorkflow(this IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        using var scope = services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<WorkflowCatalog>();
        var handlers = scope.ServiceProvider.GetServices<IWorkflowOutcomeHandler>().Select(h => h.DocumentType);
        if (WorkflowCatalog.HandlerProblems(catalog.All.Select(t => t.Code), handlers) is { Count: > 0 } problems)
        {
            throw new InvalidDataException("Workflow set-up problems: " + string.Join("; ", problems));
        }
    }
}

/// <summary>The kernel without HR: the directory answers nothing (S1.20 and S1.22 provide the HR implementation).</summary>
internal sealed class NoApproverDirectory : IApproverDirectory
{
    public bool Supports(string kind) => false;

    public Task<IReadOnlyList<Guid>> ManagersAsync(Guid userId, int levels, DateOnly asOf, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Guid>>([]);

    public Task<IReadOnlyList<Guid>> ManagersOfSubjectAsync(string subject, int levels, DateOnly asOf, DateOnly today, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Guid>>([]);

    public Task<IReadOnlyList<Guid>> DepartmentHeadsAsync(Guid userId, Guid companyId, DateOnly asOf, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Guid>>([]);

    public Task<IReadOnlyList<Guid>> PositionHoldersAsync(string position, Guid companyId, DateOnly asOf, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Guid>>([]);
}
