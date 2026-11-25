using Erp.Hrm.Application.Leave;
using Erp.Kernel.Contracts.Attendance;
using Erp.Kernel.Contracts.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Erp.Hrm.Infrastructure.Leave;

/// <summary>
/// Registers the leave engine of slice S1.32 (called from the HR employment registration of each host): the readers, the entitlement planner
/// and the recompute, the approval outcome handlers, the payroll feeds and the file owner of requests for both hosts; the leave type, ledger,
/// recompute, request, year-end and transfer services, the held year-end handler and the pack leave types for the API host; the accrual,
/// recompute, year-end and expiry jobs and the comp-off and transfer consumers for the worker. <see cref="IAttendancePeriodLock"/> is the HR module's reading implementation until S1.31 registers its own (added only when no
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

        // S1.32b: approvals of requests and cancellations (decided in the API host, escalated in the worker), payroll feeds, files of requests.
        services.AddScoped<Kernel.Contracts.Workflow.IWorkflowOutcomeHandler, LeaveRequestHandler>();
        services.AddScoped<Kernel.Contracts.Workflow.IWorkflowOutcomeHandler, LeaveCancellationHandler>();
        services.AddScoped<Contracts.Leave.ILeaveDeductions, LeaveDeductionReader>();
        services.AddScoped<Contracts.Leave.ILeaveEncashments, LeaveEncashmentReader>();
        services.AddScoped<Kernel.Contracts.Documents.IAttachmentOwner, LeaveRequestAttachmentOwner>();
        return services;
    }

    public static IServiceCollection AddHrmLeaveApi(this IServiceCollection services)
    {
        services.AddHrmLeave();
        services.AddScoped<ILeaveTypeService, LeaveTypeService>();
        services.AddScoped<ILeaveLedgerService, LeaveLedgerService>();
        services.AddScoped<ILeaveRecomputeService, LeaveRecomputeService>();
        services.AddScoped<Kernel.Contracts.Settings.ISettingChangeGuard, LeaveYearStartGuard>(); // review S1.32a finding 3
        services.AddScoped<LeaveRequestPlanner>(); // S1.32b requests, year-end, transfers, pack leave types
        services.AddScoped<ILeaveRequestService, LeaveRequestService>();
        services.AddScoped<ILeaveYearEndService, LeaveYearEndService>();
        services.AddScoped<ILeaveTransferService, LeaveTransferService>();
        services.AddScoped<Kernel.Contracts.Access.IHeldChangeHandler, YearEndConfirmHandler>();
        services.AddSingleton<Kernel.Contracts.Packs.IPackObjectHandler, LeaveTypePackHandler>();
        return services;
    }

    public static IServiceCollection AddHrmLeaveWorker(this IServiceCollection services)
    {
        services.AddHrmLeave();
        services.AddSingleton<IJobHandler, LeaveAccrualJob>();
        services.AddSingleton<IJobHandler, LeaveRecomputeJob>();
        services.AddSingleton<IJobHandler, LeaveYearEndJob>(); // S1.32b
        services.AddSingleton<IJobHandler, LeaveExpiryJob>();
        services.AddScoped<IEventConsumer, CompOffConsumer>();
        services.AddScoped<IEventConsumer, LeaveTransferConsumer>();
        return services;
    }
}
