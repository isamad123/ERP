using System.Text.Json;
using Erp.Kernel.Contracts.Generated;
using Erp.Kernel.Domain.Workflow;
using Erp.Kernel.Infrastructure.Settings;

namespace Erp.Kernel.Infrastructure.Workflow;

/// <summary>
/// The working calendar of a company as approval time limits use it (slice S1.10 rulings Q9, Q16; S1.10b rulings P3, P4):
/// the company's time zone (<c>org.time_zone</c>), its weekly off days (<c>org.weekly_off_days</c>, default Saturday and
/// Sunday), the public and religious holidays of its holiday calendar, and the default limit of a step that sets none
/// (<c>wf.escalation.default_working_days</c>, default 2).
/// </summary>
internal sealed record CompanyCalendar(TimeZoneInfo Zone, IReadOnlySet<DayOfWeek> OffDays, IReadOnlySet<DateOnly> Holidays, int DefaultWorkingDays)
{
    public DateTimeOffset DueAt(DateTimeOffset arrived, int? workingDays) =>
        WorkingCalendar.DueAt(arrived, workingDays ?? DefaultWorkingDays, Zone, OffDays, Holidays);
}

/// <summary>Reads company calendars on the work's own transaction (cached for the unit of work).</summary>
internal static class WorkflowTime
{
    public const int FallbackWorkingDays = 2;

    private static readonly string[] Keys = [KernelSettingKeys.OrgTimeZone, KernelSettingKeys.OrgWeeklyOffDays, KernelSettingKeys.WfEscalationDefaultWorkingDays];

    public static async Task<CompanyCalendar> CalendarAsync(WorkflowWork work, Guid companyId, CancellationToken ct)
    {
        if (work.Calendars.TryGetValue(companyId, out var cached))
        {
            return cached;
        }

        var today = DateOnly.FromDateTime(work.Now.UtcDateTime);
        var settings = await SettingsStore.ResolveAsync(work.Connection, work.Transaction, Keys, companyId, null, today, ct).ConfigureAwait(false);
        var zone = WorkingCalendar.Zone(settings.TryGetValue(KernelSettingKeys.OrgTimeZone, out var z) && z.Value.ValueKind == JsonValueKind.String ? z.Value.GetString() : "Asia/Karachi");
        var offDays = settings.TryGetValue(KernelSettingKeys.OrgWeeklyOffDays, out var o) && o.Value.ValueKind == JsonValueKind.Array
            ? WorkingCalendar.ParseOffDays(o.Value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!))
            : [DayOfWeek.Saturday, DayOfWeek.Sunday];
        var days = settings.TryGetValue(KernelSettingKeys.WfEscalationDefaultWorkingDays, out var d) && d.Value.ValueKind == JsonValueKind.Number && d.Value.TryGetInt32(out var n) && n >= 1
            ? n
            : FallbackWorkingDays;

        var holidays = new HashSet<DateOnly>();
        await using (var command = work.Command("""
            select h.holiday_date from org.companies c
            join ref.holidays h on h.calendar_id = c.holiday_calendar_id
            where c.id = $1 and h.kind = any($2) and h.holiday_date between $3 and $4
            """, companyId, WorkingCalendar.CountedHolidayKinds.ToArray(), today.AddDays(-400), today.AddDays(WorkingCalendar.MaxDaysSearched)))
        await using (var r = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                holidays.Add(r.GetFieldValue<DateOnly>(0));
            }
        }

        var calendar = new CompanyCalendar(zone, offDays, holidays, days);
        work.Calendars[companyId] = calendar;
        return calendar;
    }
}
