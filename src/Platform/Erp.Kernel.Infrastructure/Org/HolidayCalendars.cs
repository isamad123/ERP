using System.Text.Json;
using Erp.Kernel.Contracts.Org;
using Erp.Kernel.Domain.Generated.Org;
using Erp.Kernel.Domain.Generated.Ref;
using Erp.Kernel.Infrastructure.Generated;
using Microsoft.EntityFrameworkCore;

namespace Erp.Kernel.Infrastructure.Org;

/// <summary>Holiday calendar answers for business modules (contract <c>IHolidayCalendars</c>, slice S1.28b).</summary>
public sealed class HolidayCalendars(KernelDbContext db) : IHolidayCalendars
{
    public async Task<Guid?> CalendarOfAsync(Guid companyId, Guid? locationId, CancellationToken cancellationToken)
    {
        if (locationId is { } location)
        {
            var own = await db.Set<Location>().AsNoTracking().Where(l => l.Id == location && l.CompanyId == companyId && !l.IsDeleted)
                .Select(l => l.HolidayCalendarId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (own is not null)
            {
                return own;
            }
        }

        return await db.Set<Company>().AsNoTracking().Where(c => c.Id == companyId && !c.IsDeleted)
            .Select(c => c.HolidayCalendarId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<HolidayInfo>> HolidaysAsync(Guid calendarId, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken)
    {
        var rows = await db.Set<Holiday>().AsNoTracking().Where(h => h.CalendarId == calendarId && h.HolidayDate >= firstDay && h.HolidayDate <= lastDay)
            .OrderBy(h => h.HolidayDate).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(Map)];
    }

    public async Task<IReadOnlyList<HolidayInfo>> FindAsync(IReadOnlyCollection<Guid> holidayIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(holidayIds);
        var ids = holidayIds.Distinct().ToList();
        var rows = await db.Set<Holiday>().AsNoTracking().Where(h => ids.Contains(h.Id)).OrderBy(h => h.HolidayDate).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(Map)];
    }

    private static HolidayInfo Map(Holiday h) => new(h.Id, h.CalendarId, h.HolidayDate, Names(h.NameI18n), h.Kind, h.IsTentative);

    private static Dictionary<string, string> Names(JsonDocument name) =>
        name.RootElement.ValueKind == JsonValueKind.Object
            ? name.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal)
            : [];
}
