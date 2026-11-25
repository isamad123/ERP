using Erp.Hrm.Application.Expenses;
using Erp.Hrm.Contracts.Expenses;
using Erp.Hrm.Contracts.Payroll;
using Erp.Kernel.Contracts.Documents;
using Erp.Kernel.Contracts.Workflow;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Erp.Hrm.Infrastructure.Expenses;

/// <summary>
/// Registers expense claims of slice S1.42 (called from the HR employment registration of each host): the approval outcome handler (decided in the API host,
/// escalated in the worker), the file owner of claims, the payroll provider of approved claims, the payment return and the open-claims contract for both
/// hosts; the claim service and its policy reader for the API host (<see cref="AddHrmExpenses"/> is called once by the HR employment registration).
/// </summary>
public static class ExpenseServices
{
    public static IServiceCollection AddHrmExpenses(this IServiceCollection services)
    {
        services.AddScoped<IWorkflowOutcomeHandler, ExpenseClaimHandler>();
        services.AddScoped<IAttachmentOwner, ExpenseClaimAttachmentOwner>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IPayInputProvider, ExpenseInputProvider>());
        services.TryAddScoped<IExpensePaymentReturn, ExpensePaymentReturnService>();
        services.TryAddScoped<IExpenseOpenClaims, ExpenseOpenClaimsReader>();
        return services;
    }

    public static IServiceCollection AddHrmExpensesApi(this IServiceCollection services)
    {
        services.TryAddScoped<ExpensePolicyReader>();
        services.TryAddScoped<IExpenseClaimService, ExpenseClaimService>();
        return services;
    }
}
