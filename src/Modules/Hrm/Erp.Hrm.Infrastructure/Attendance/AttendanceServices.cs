using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Contracts.Attendance;
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
        return services;
    }

    /// <summary>The attendance endpoints' services (API host).</summary>
    public static IServiceCollection AddHrmAttendanceApi(this IServiceCollection services)
    {
        services.AddScoped<IAttendanceCaptureService, AttendanceCaptureService>();
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
        services.AddSingleton<IJobHandler, AttendancePurgeJob>();
        return services;
    }
}
