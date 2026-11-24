using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Contracts.Attendance;
using Erp.Hrm.Infrastructure.Devices;
using Erp.Kernel.Contracts.Jobs;
using Microsoft.Extensions.DependencyInjection;

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
        return services;
    }

    /// <summary>The attendance endpoints' services (API host).</summary>
    public static IServiceCollection AddHrmAttendanceApi(this IServiceCollection services)
    {
        services.AddScoped<IAttendanceCaptureService, AttendanceCaptureService>();
        services.AddScoped<IDeviceService, DeviceService>(); // S1.27b
        services.AddScoped<IDeviceMappingService, DeviceMappingService>();
        services.AddScoped<IDevicePushService, DevicePushService>();
        return services;
    }

    /// <summary>The shift and roster services (slice S1.28a), for the API host: shifts, rosters and the shift policy reader.</summary>
    public static IServiceCollection AddHrmRostering(this IServiceCollection services)
    {
        services.AddScoped<ShiftPolicyReader>();
        services.AddScoped<IShiftService, ShiftService>();
        services.AddScoped<IRosterService, RosterService>();
        return services;
    }

    /// <summary>The attendance job of the worker host.</summary>
    public static IServiceCollection AddHrmAttendanceWorker(this IServiceCollection services)
    {
        services.AddHrmAttendance(); // the event store and the device drivers the sync job uses
        services.AddSingleton<IJobHandler, AttendancePurgeJob>();
        services.AddSingleton<IJobHandler, DeviceSyncJob>(); // S1.27b
        services.AddScoped<IEventConsumer, DeviceSeparationConsumer>();
        return services;
    }
}
