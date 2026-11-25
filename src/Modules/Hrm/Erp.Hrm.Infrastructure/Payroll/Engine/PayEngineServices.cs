using Erp.Hrm.Application.Payroll;
using Erp.Hrm.Contracts.Payroll;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Erp.Hrm.Infrastructure.Payroll.Engine;

/// <summary>
/// Registers the payroll engine of slice S1.35a: the snapshot builder, the input providers of this slice with their named inputs, the run and input
/// services. Later providers (S1.41 loans, S1.42 expenses) and statutory calculators (S1.36 tax, S1.37 contributions) add theirs next to these in the
/// registrations both hosts call (S1.35b registers the engine in the worker host for the chunked job).
/// </summary>
public static class PayEngineServices
{
    public static IServiceCollection AddHrmPayrollEngine(this IServiceCollection services)
    {
        services.TryAddScoped<PaySnapshotBuilder>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IPayInputProvider, AttendanceLeaveInputProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IPayInputProvider, OvertimeInputProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IPayInputProvider, LatenessInputProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IPayInputProvider, AdjustmentInputProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IPayInputProvider, PayInputRowsProvider>());
        services.AddSingleton(AttendanceLeaveInputProvider.Names);
        services.AddSingleton(OvertimeInputProvider.Names);
        services.AddSingleton(LatenessInputProvider.Names);
        services.AddSingleton(AdjustmentInputProvider.Names);
        services.TryAddScoped<IPayRunService, PayRunService>();
        services.TryAddScoped<IPayInputService, PayInputService>();
        return services;
    }
}
