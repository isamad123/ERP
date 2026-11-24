using System.Globalization;
using System.Text.Json;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Attendance;
using Erp.Hrm.Contracts.Generated;
using Erp.Hrm.Contracts.Policies;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Kernel.Contracts.Org;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hrm.Infrastructure.Attendance;

/// <summary>
/// The shift an employment works on a day, and why (slice S1.28b Build item 8; FR-HRM-ATT-16, ruling Q6). The day comes from the published
/// roster; with no roster day the weekly off of the <c>shift</c> policy and then the holiday calendar say what the day is. A
/// <c>seasonal_timing</c> policy in force that day (resolved through <see cref="IPolicyResolver"/>, so it applies to its employee group only)
/// changes the shift's times without touching the shift. Nothing is guessed: a day nothing speaks for is <c>none</c>. Holiday handling
/// for pay belongs to day processing (S1.29), which reads this answer.
/// </summary>
internal sealed class EffectiveShiftService(
    HrmDbContext db,
    IPermissionChecker permissions,
    IDataScopeFilter scope,
    EffectiveShiftReader reader) : IEffectiveShiftService, IEffectiveShiftReader
{
    private DbContextScopeSource Source => new(db);

    public async Task<PeopleResult> EffectiveAsync(Guid employmentId, DateOnly day, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(HolidayPermissions.EffectiveShiftRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var employment = await db.Set<Employment>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employmentId, cancellationToken).ConfigureAwait(false);
        if (employment is null || !await scope.AllowsAsync(employment, EmploymentAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        var answer = await reader.ReadAsync(employmentId, day, cancellationToken).ConfigureAwait(false);
        return answer is null ? PeopleResult.NotFound : new PeopleResult(PeopleOutcome.Done, Body: answer);
    }

    public Task<EffectiveShift?> ReadAsync(Guid employmentId, DateOnly day, CancellationToken cancellationToken) => reader.ReadAsync(employmentId, day, cancellationToken);
}

/// <summary>
/// The answer of <see cref="IEffectiveShiftReader"/> without a permission or scope check (the endpoint's service checks those): used by the
/// endpoint and by day processing (S1.29a, R-158), which builds it on the job's own HR database connection.
/// </summary>
internal sealed class EffectiveShiftReader(
    HrmDbContext db,
    IPolicyResolver resolver,
    IHolidayCalendars calendars,
    AttendanceSettingReader settings) : IEffectiveShiftReader
{
    private readonly ShiftPolicyReader shiftPolicies = new(resolver);

    public async Task<EffectiveShift?> ReadAsync(Guid employmentId, DateOnly day, CancellationToken cancellationToken)
    {
        var employment = await db.Set<Employment>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employmentId, cancellationToken).ConfigureAwait(false);
        if (employment is null)
        {
            return null;
        }

        var reasons = new List<EffectiveReason>();
        var holiday = await HolidayAsync(employment, day, cancellationToken).ConfigureAwait(false);
        if (holiday is not null)
        {
            reasons.Add(new EffectiveReason("hrm.shift.effective.reason.holiday", new Dictionary<string, string> { ["kind"] = holiday.Kind }));
        }

        if (day < employment.JoiningDate || (employment.SeparationDate is { } end && day > end))
        {
            reasons.Insert(0, new EffectiveReason("hrm.shift.effective.reason.outside_employment"));
            return Empty(employmentId, day, DayTypes.None, "none", holiday, reasons);
        }

        var line = await (from l in db.Set<RosterLine>().AsNoTracking()
                          where l.EmploymentId == employmentId && l.WorkDate == day && l.RosterStatus == "published"
                          select l).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (line is not null)
        {
            if (line.DayType != DayTypes.Working || line.ShiftId is not { } shiftId)
            {
                reasons.Insert(0, new EffectiveReason("hrm.shift.effective.reason.roster_day", new Dictionary<string, string> { ["dayType"] = line.DayType }));
                return Empty(employmentId, day, line.DayType, "roster", holiday, reasons) with { RosterId = line.RosterId };
            }

            var shift = await db.Set<Shift>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == shiftId, cancellationToken).ConfigureAwait(false);
            if (shift is not null)
            {
                reasons.Insert(0, new EffectiveReason("hrm.shift.effective.reason.roster_shift", new Dictionary<string, string> { ["shift"] = shift.Code }));
                return await WithSeasonAsync(employment, day, shift, line.RosterId, holiday, reasons, cancellationToken).ConfigureAwait(false);
            }
        }

        var policy = await shiftPolicies.ReadAsync(employmentId, day, day, cancellationToken).ConfigureAwait(false);
        if (policy?.For(day).IsOff(day) == true)
        {
            reasons.Insert(0, new EffectiveReason("hrm.shift.effective.reason.weekly_off_policy", new Dictionary<string, string> { ["policy"] = policy.For(day).PolicyCode ?? string.Empty }));
            return Empty(employmentId, day, DayTypes.WeeklyOff, "weekly_off_policy", holiday, reasons);
        }

        if (holiday is not null)
        {
            return Empty(employmentId, day, DayTypes.Holiday, "holiday_calendar", holiday, reasons);
        }

        reasons.Insert(0, new EffectiveReason("hrm.shift.effective.reason.no_roster"));
        return Empty(employmentId, day, DayTypes.None, "none", null, reasons);
    }

    private async Task<EffectiveShift> WithSeasonAsync(
        Employment employment, DateOnly date, Shift shift, Guid rosterId, HolidayView? holiday, List<EffectiveReason> reasons, CancellationToken cancellationToken)
    {
        var segments = ShiftRules.ParseSegments(shift.Segments.RootElement) ?? [];
        var breakMinutes = segments.Where(s => s.Kind == ShiftSegment.Break).Sum(s => ShiftRules.SpanMinutes(s.Start, s.End, s.End <= s.Start));
        var times = new TimingResult(shift.StartTime, shift.EndTime, breakMinutes, shift.CrossesMidnight);
        AppliedSeason? applied = null;

        var resolution = await resolver.ResolveAsync(employment.Id, SeasonalTiming.PolicyType, date, cancellationToken).ConfigureAwait(false);
        if (resolution?.Policy is { } match)
        {
            var offset = await settings.IntAsync(HrmSettingKeys.HrmAttHijriOffsetDays, employment.CompanyId, 0, cancellationToken).ConfigureAwait(false);
            var (rules, errors) = SeasonalTiming.Parse(match.Parameters);
            if (errors.Count == 0 && SeasonalTiming.Find(rules, date, HijriDates.ValidOffset(offset) ? offset : 0) is { } season)
            {
                times = SeasonalTiming.Apply(season, shift.StartTime, shift.EndTime, shift.CrossesMidnight, breakMinutes);
                applied = new AppliedSeason(match.PolicyId, match.PolicyCode, season.Name, times.Start, times.End, times.BreakMinutes, season.ShortenPercent);
                reasons.Insert(1, new EffectiveReason("hrm.shift.effective.reason.seasonal", new Dictionary<string, string>
                {
                    ["policy"] = match.PolicyCode,
                    ["season"] = season.Name,
                    ["start"] = times.Start?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? string.Empty,
                    ["end"] = times.End?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? string.Empty,
                }));
            }
        }

        var name = shift.NameI18n.RootElement.ValueKind == JsonValueKind.Object
            ? shift.NameI18n.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal)
            : [];
        return new EffectiveShift(employment.Id, date, DayTypes.Working, "roster", rosterId, shift.Id, shift.Code, name, shift.ShiftType, times.Start, times.End,
            times.BreakMinutes, times.CrossesMidnight, shift.StartTime, shift.EndTime, applied, holiday, reasons);
    }

    private static EffectiveShift Empty(Guid employmentId, DateOnly date, string dayType, string source, HolidayView? holiday, List<EffectiveReason> reasons) =>
        new(employmentId, date, dayType, source, null, null, null, null, null, null, null, null, false, null, null, null, holiday, reasons);

    /// <summary>The holiday of the day in the employment's calendar that applies to the employee: a mandatory one, or an optional one the employee chose.</summary>
    private async Task<HolidayView?> HolidayAsync(Employment employment, DateOnly date, CancellationToken cancellationToken)
    {
        var location = await db.Set<EmploymentAssignment>().AsNoTracking()
            .Where(a => a.EmploymentId == employment.Id && a.ValidFrom <= date && (a.ValidTo == null || a.ValidTo > date))
            .Select(a => a.LocationId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var calendar = await calendars.CalendarOfAsync(employment.CompanyId, location, cancellationToken).ConfigureAwait(false);
        if (calendar is not { } id)
        {
            return null;
        }

        var holidays = await calendars.HolidaysAsync(id, date, date, cancellationToken).ConfigureAwait(false);
        var offset = await settings.IntAsync(HrmSettingKeys.HrmAttHijriOffsetDays, employment.CompanyId, 0, cancellationToken).ConfigureAwait(false);
        foreach (var h in holidays)
        {
            var optional = string.Equals(h.Kind, HolidayService.OptionalKind, StringComparison.Ordinal);
            if (!optional || await db.Set<OptionalHolidayChoice>().AsNoTracking().AnyAsync(c => c.EmploymentId == employment.Id && c.HolidayId == h.Id, cancellationToken).ConfigureAwait(false))
            {
                return HolidayService.View(h, HijriDates.ValidOffset(offset) ? offset : 0);
            }
        }

        return null;
    }
}
