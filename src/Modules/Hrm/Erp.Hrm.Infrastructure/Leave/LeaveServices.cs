using Erp.Hrm.Application.Leave;
using Erp.Kernel.Contracts.Attendance;
using Erp.Kernel.Contracts.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Erp.Hrm.Infrastructure.Leave;

/// <summary>
/// Registers the leave engine of slice S1.32a (called from the HR employment registration of each host): the readers, the entitlement planner
/// and the recompute for both hosts, the leave type, ledger and recompute services for the API host, the accrual and recompute jobs for the
/// worker. <see cref="IAttendancePeriodLock"/> is the HR module's reading implementation until S1.31 registers its own (added only when no
/// other is registered).
/// </summary>
public static class LeaveServices
{
    public static IServiceCollection AddHrmLeave(this IServiceCollection services)
    {
        services.TryAddScoped<IAttendancePeriodLock, LockedPeriodReader>();
        services.AddScoped<LeavePolicyReader>();
        services.AddScoped<LeaveYearSetting>();
        services.AddScoped<WorkedTimeReader>();
        services.AddScoped<EntitlementPlanner>();
        services.AddScoped<LeaveRecompute>();
        return services;
    }

    public static IServiceCollection AddHrmLeaveApi(this IServiceCollection services)
    {
        services.AddHrmLeave();
        services.AddScoped<ILeaveTypeService, LeaveTypeService>();
        services.AddScoped<ILeaveLedgerService, LeaveLedgerService>();
        services.AddScoped<ILeaveRecomputeService, LeaveRecomputeService>();
        return services;
    }

    public static IServiceCollection AddHrmLeaveWorker(this IServiceCollection services)
    {
        services.AddHrmLeave();
        services.AddSingleton<IJobHandler, LeaveAccrualJob>();
        services.AddSingleton<IJobHandler, LeaveRecomputeJob>();
        return services;
    }
}
