using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Contracts.Attendance;
using Erp.Hrm.Infrastructure.Devices;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Contracts.Attendance;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Erp.Hrm.Infrastructure.Attendance;

/// <summary>Registers the attendance capture services of slice S1.27a. The API host adds the capture service, the worker host the purge job.</summary>
public static class AttendanceServices
{
    /// <summary>What both hosts need: the event store (later also used by the device connectors) and its settings reader.</summary>
    public static IServiceCollection AddHrmAttendance(this IServiceCollection services)
    {
        services.AddScoped<AttendanceSettingReader>();
        services.AddScoped<IAttendanceEventStore, AttendanceEventStore>();
        services.AddSingleton<IAttendanceDeviceDriver, GenericPushDriver>(); // S1.27b device drivers (the extension point of ATT-03)
        services.AddSingleton<IAttendanceDeviceDriver, ZkTecoDriver>();
        services.AddScoped<DeviceRuntime>();
        services.AddScoped<DeviceIngestion>();

        // S1.29a attendance engine: approved leave and duty by day (S1.32b and S1.30 write the requests), re-queueing, the processor.
        services.TryAddScoped<IAttendancePeriodLock, Leave.LockedPeriodReader>();
        services.TryAddScoped<IApprovedLeaveDays, ApprovedLeaveDaysReader>();
        services.TryAddScoped<IApprovedDutyDays, ApprovedDutyDaysReader>();
        services.TryAddScoped<IAttendanceReprocessing, AttendanceReprocessing>();
        services.AddScoped<EffectiveShiftReader>(); // S1.28b's effective shift is the engine's day plan (R-158); needs IHolidayCalendars from the host
        services.AddScoped<IEffectiveShiftReader>(sp => sp.GetRequiredService<EffectiveShiftReader>());
        services.AddScoped<AttendanceConsequences>(); // S1.29b: overtime entries and lateness penalties follow the results
        services.AddScoped<AttendanceDayProcessor>();
        services.TryAddScoped<IOvertimeInputs, OvertimeInputReader>(); // S1.29b payroll hand-off (S1.35 reads both)
        services.TryAddScoped<ILatenessDeductions, LatenessDeductionReader>();

        // S1.30a: the end of an approval of a regularization or duty request (an approval can be decided through either host).
        services.AddScoped<Kernel.Contracts.Workflow.IWorkflowOutcomeHandler, RegularizationHandler>();
        services.AddScoped<Kernel.Contracts.Workflow.IWorkflowOutcomeHandler, DutyRequestHandler>();
        services.AddScoped<Kernel.Contracts.Workflow.IWorkflowOutcomeHandler, OvertimeHandler>(); // S1.29b overtime and waivers
        services.AddScoped<Kernel.Contracts.Workflow.IWorkflowOutcomeHandler, PenaltyWaiverHandler>();
        return services;
    }

    /// <summary>The attendance endpoints' services (API host).</summary>
    public static IServiceCollection AddHrmAttendanceApi(this IServiceCollection services)
    {
        services.AddScoped<IAttendanceCaptureService, AttendanceCaptureService>();
        services.AddScoped<IDeviceService, DeviceService>(); // S1.27b
        services.AddScoped<IDeviceMappingService, DeviceMappingService>();
        services.AddScoped<IDevicePushService, DevicePushService>();
        services.AddScoped<IAttendanceDayService, AttendanceDayService>(); // S1.29a
        services.AddScoped<IAttendanceRequestService, AttendanceRequestService>(); // S1.30a
        services.AddScoped<IAttendanceConsequenceService, AttendanceConsequenceService>(); // S1.29b
        return services;
    }

    /// <summary>The shift and roster services (slice S1.28a), for the API host: shifts, rosters and the shift policy reader.</summary>
    public static IServiceCollection AddHrmRostering(this IServiceCollection services)
    {
        services.AddScoped<ShiftPolicyReader>();
        services.AddScoped<IShiftService, ShiftService>();
        services.AddScoped<IRosterService, RosterService>();
        services.AddScoped<IHolidayService, HolidayService>(); // S1.28b: Hijri dates and optional holidays
        services.AddScoped<EffectiveShiftService>();
        services.AddScoped<IEffectiveShiftService>(sp => sp.GetRequiredService<EffectiveShiftService>());
        return services;
    }

    /// <summary>The attendance jobs and consumers of the worker host.</summary>
    public static IServiceCollection AddHrmAttendanceWorker(this IServiceCollection services)
    {
        services.AddHrmAttendance(); // the event store and the device drivers the sync job uses
        services.AddSingleton<IJobHandler, AttendancePurgeJob>();
        services.AddSingleton<IJobHandler, DeviceSyncJob>(); // S1.27b
        services.AddScoped<IEventConsumer, DeviceSeparationConsumer>();
        services.AddSingleton<IJobHandler, AttendanceProcessJob>(); // S1.29a nightly and on-demand processing
        services.AddScoped<IEventConsumer, AttendanceReprocessConsumer>(); // S1.29a re-queued days
        return services;
    }
}
