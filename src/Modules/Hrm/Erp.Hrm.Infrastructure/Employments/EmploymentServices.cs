using Erp.Hrm.Application.Employments;
using Erp.Kernel.Contracts.Entitlements;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Contracts.Workflow;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Hrm.Infrastructure.Employments;

/// <summary>
/// Registers the employment services of slice S1.20b. Both hosts get the document type <c>hrm.employment_event</c> with its outcome
/// handler (escalation runs in the worker), the approver directory (line managers, ruling Q7), the employee counters (the limit at
/// hire, the monthly meter; S1.09b ruling Q8) and the HR job manifest; the worker also gets the job's handler.
/// </summary>
public static class EmploymentServices
{
    /// <summary>The HR module's workflow manifest (embedded <c>Access/workflow.json</c>).</summary>
    public static string WorkflowManifest { get; } = Embedded("Erp.Hrm.Infrastructure.Access.workflow.json");

    /// <summary>The HR module's notification catalogue (embedded <c>Access/notification-events.json</c>, S1.20c); each host registers it.</summary>
    public static string NotificationManifest { get; } = Embedded("Erp.Hrm.Infrastructure.Access.notification-events.json");

    /// <summary>The HR module's job manifest (embedded <c>Access/jobs.json</c>).</summary>
    public static string JobManifest { get; } = Embedded("Erp.Hrm.Infrastructure.Access.jobs.json");

    /// <summary>
    /// What both hosts need. The API host adds <see cref="AddHrmEmploymentApi"/>, the worker <see cref="AddHrmEmploymentWorker"/>; each
    /// host registers <see cref="WorkflowManifest"/> as a workflow document-type source (the manifest reader is the kernel's).
    /// </summary>
    public static IServiceCollection AddHrmEmployment(this IServiceCollection services)
    {
        services.AddScoped<EmploymentApplier>();
        services.AddScoped<EmploymentChangeCheck>();
        services.AddScoped<IWorkflowOutcomeHandler, EmploymentEventHandler>();
        services.AddScoped<IApproverDirectory, HrmApproverDirectory>();
        services.AddSingleton<ILimitCounter, ActiveEmployeeLimitCounter>();
        services.AddSingleton<IActiveEmployeeCounter, ActiveEmployeeCounter>();
        services.AddSingleton(new JobManifestSource(JobManifest));
        return services;
    }

    /// <summary>The employment services of the API host.</summary>
    public static IServiceCollection AddHrmEmploymentApi(this IServiceCollection services)
    {
        services.AddScoped<EmploymentService>();
        services.AddScoped<IEmploymentService>(sp => sp.GetRequiredService<EmploymentService>());
        services.AddScoped<IEmploymentMoves>(sp => sp.GetRequiredService<EmploymentService>());
        services.AddScoped<IEmploymentTermsService, EmploymentTermsService>();
        services.AddScoped<IEmployeeBankAccountService, EmployeeBankAccountService>(); // S1.20d payment accounts
        services.AddScoped<IStatutoryRegistrationService, StatutoryRegistrationService>();
        services.AddScoped<Erp.Kernel.Contracts.Access.IHeldChangeHandler, BankAccountChangeHandler>(); // approval of held account changes
        return services;
    }

    /// <summary>The daily jobs of the worker host (ruling Q12; S1.20c contract and probation reminders).</summary>
    public static IServiceCollection AddHrmEmploymentWorker(this IServiceCollection services)
    {
        services.AddSingleton<IJobHandler, EmploymentEventsDueJob>();
        services.AddSingleton<IJobHandler, EmploymentTermsDueJob>();
        return services;
    }

    private static string Embedded(string name)
    {
        using var stream = typeof(EmploymentServices).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException(name + " is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
