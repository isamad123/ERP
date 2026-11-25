using Erp.Hrm.Application.Payroll;
using Erp.Hrm.Contracts.Payroll;
using Erp.Kernel.Contracts.Access;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Contracts.Workflow;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Hrm.Infrastructure.Payroll;

/// <summary>Registers the pay set-up services of slices S1.34a and S1.34b (called from the HR employment registration of each host).</summary>
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
        // S1.34b names and cycles; S1.35a completes the grammar and knows the named inputs of every registered provider (R-282).
        services.AddSingleton<IFormulaValidator>(sp => new FormulaValidator(sp.GetServices<PayInputNameSet>()));
        Engine.PayEngineServices.AddHrmPayrollEngine(services); // S1.35a runs, calculation, inputs
        services.AddScoped<IPayComponentService, PayComponentService>();
        services.AddScoped<ISalaryStructureService, SalaryStructureService>();
        services.AddScoped<ICompensationService, CompensationService>();
        services.AddScoped<IHeldChangeHandler, StructureChangeHandler>();
        services.AddScoped<IHeldChangeHandler, CompensationChangeHandler>();
        services.AddScoped<IPayReadinessCheck, BankDataCompleteCheck>();
        services.AddScoped<IPayReadinessCheck, StructuresAssignedCheck>();
        services.AddScoped<IPayReadinessCheck, Attendance.AttendanceLockedCheck>(); // S1.31 (FR-XM-26)
        services.AddHrmLoans(); // S1.41 loan types, applications, recovery and reports
        return services;
    }

    /// <summary>
    /// The loan services of slice S1.41: types, applications and their approval, the payout, receipts, the reports, the payroll input provider (due installments become deductions),
    /// the posting of a locked run and the clearance contract. The provider and the posting are also what the worker host needs; both hosts call this through the HR registration.
    /// </summary>
    public static IServiceCollection AddHrmLoans(this IServiceCollection services)
    {
        services.AddScoped<LoanFacts>();
        services.AddScoped<ILoanTypeService, LoanTypeService>();
        services.AddScoped<ILoanService, LoanService>();
        services.AddScoped<ILoanReportService, LoanReportService>();
        services.AddScoped<IPayReadinessCheck, LoansUpdatedCheck>(); // the readiness item loans_updated
        services.AddScoped<IWorkflowOutcomeHandler, LoanApplicationHandler>();
        services.AddScoped<IHeldChangeHandler, LoanReceiptHandler>();
        services.AddScoped<IPayInputProvider, LoanInputProvider>();
        services.AddScoped<ILoanRecoveryPosting, LoanRecoveryPosting>();
        services.AddScoped<IOutstandingLoans, OutstandingLoans>();
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
