using System.Data.Common;
using System.Text.Json;
using Erp.BuildingBlocks.Domain.Time;
using Erp.Hrm.Contracts.Attendance;
using Erp.Hrm.Contracts.Employments;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Contracts.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Hrm.Infrastructure.Devices;

/// <summary>
/// Leaving an employment ends its device mappings and makes the removal tasks (slice S1.27b Build item 8; FR-HRM-ATT-20): on <c>hrm.employee_separated</c> every
/// mapping of the employment that is still open ends on the first day without the employment, and a mapping of one device raises
/// <c>hrm.device_user.removal_due</c> (the user is to be removed from the device by hand, and HR confirms it). Runs once per event in the
/// dispatcher's transaction; the mapping rows change through the audited context. A mapping for all devices (a badge number) just ends.
/// </summary>
public sealed class DeviceSeparationConsumer(IServiceProvider services, IClock clock) : IEventConsumer
{
    public string Name => "hrm.device_separation";

    public bool Accepts(string eventType) => eventType == EmploymentEvents.EmployeeSeparated;

    public async Task HandleAsync(OutboxEvent message, DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var payload = message.Payload;
        var employmentId = payload.GetProperty("employmentId").GetGuid();
        var lastDay = DateOnly.Parse(payload.GetProperty("separationDate").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        var end = lastDay.AddDays(1);
        var now = clock.UtcNow;
        var actor = message.Headers.ValueKind == JsonValueKind.Object && message.Headers.TryGetProperty("actor", out var a) && a.ValueKind == JsonValueKind.String && Guid.TryParse(a.GetString(), out var g) ? g : Guid.Empty;
        await using var db = new HrmDbContext(services.GetRequiredService<DbContextOptions<HrmDbContext>>());
        db.Database.SetDbConnection(connection, contextOwnsConnection: false);
        await db.Database.UseTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
        var applier = new EmploymentApplier(db, services.GetRequiredService<ISettings>(), Policies.PolicyResolver.For(db, services));
        var maps = await db.Set<DeviceUserMap>().Where(m => m.EmploymentId == employmentId && (m.ValidTo == null || m.ValidTo > end)).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (maps.Count == 0)
        {
            return;
        }

        var employeeNo = await db.Set<Employment>().AsNoTracking().Where(e => e.Id == employmentId).Select(e => e.EmployeeNo).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        foreach (var map in maps)
        {
            map.ValidTo = end > map.ValidFrom ? end : map.ValidFrom.AddDays(1);
            if (map.DeviceId is { } device && map.RemovalDueAt is null)
            {
                map.RemovalDueAt = now;
                var serial = await db.Set<AttendanceDevice>().AsNoTracking().Where(x => x.Id == device).Select(x => x.SerialNo).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                applier.RaiseFor(DeviceEvents.RemovalDue, DeviceEvents.AggregateType, map.Id, now, actor, new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["map_id"] = map.Id,
                    ["device_id"] = device,
                    ["device_serial"] = serial,
                    ["device_user_no"] = map.DeviceUserNo,
                    ["employee_no"] = employeeNo,
                    ["company_id"] = map.CompanyId,
                    ["link"] = "/time/devices?tab=maps",
                });
            }

            EmploymentApplier.Bump(map, now, actor);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
