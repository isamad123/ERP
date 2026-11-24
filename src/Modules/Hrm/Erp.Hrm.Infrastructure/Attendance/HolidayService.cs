using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Attendance;
using Erp.Hrm.Contracts.Generated;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Kernel.Contracts.Org;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Erp.Hrm.Infrastructure.Attendance;

/// <summary>
/// Holidays with Hijri dates and the choice of optional holidays (slice S1.28b Build items 6 and 7; FR-HRM-ATT-15). The calendars are the
/// kernel's (read through <see cref="IHolidayCalendars"/>, D-10); this service adds the Hijri date (Umm al-Qura moved by the company's
/// <c>hrm.att.hijri_offset_days</c>, display only) and keeps <c>hrm.optional_holiday_choices</c>. An optional holiday is a holiday of kind
/// <c>optional</c>; how many an employee may choose a year (<c>hrm.att.optional_holidays_per_year</c>, none until the customer sets it) and
/// how long before the holiday a choice can still change (<c>hrm.att.optional_holiday_cutoff_days</c>) are settings of the company.
/// </summary>
internal sealed class HolidayService(
    HrmDbContext db,
    IClock clock,
    ICurrentActor actor,
    IPermissionChecker permissions,
    IDataScopeFilter scope,
    IHolidayCalendars calendars,
    IOrgStructure org,
    AttendanceSettingReader settings) : IHolidayService
{
    public const string YearInvalid = "errors.hrm.holiday.year_invalid";
    public const string NotOptional = "errors.hrm.holiday.not_optional";
    public const string NotInCalendar = "errors.hrm.holiday.not_in_calendar";
    public const string LimitExceeded = "errors.hrm.holiday.limit_exceeded";
    public const string CutoffPassed = "errors.hrm.holiday.cutoff_passed";
    public const string OutsideEmployment = "errors.hrm.holiday.outside_employment";
    public const string ChoiceConflict = "errors.hrm.holiday.choice_conflict";
    public const string OptionalKind = "optional";

    private DbContextScopeSource Source => new(db);

    private DateOnly Today => DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

    public async Task<PeopleResult> ListAsync(Guid companyId, Guid? locationId, Guid? calendarId, int year, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(HolidayPermissions.HolidaysRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (year is < 1900 or > 2200)
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["year"] = [YearInvalid] });
        }

        if ((await org.FindAsync(OrgRecordKind.Company, [companyId], cancellationToken).ConfigureAwait(false)).Count == 0)
        {
            return PeopleResult.NotFound;
        }

        var calendar = calendarId ?? await calendars.CalendarOfAsync(companyId, locationId, cancellationToken).ConfigureAwait(false);
        var offset = await HijriOffsetAsync(companyId, cancellationToken).ConfigureAwait(false);
        var holidays = calendar is { } id
            ? await calendars.HolidaysAsync(id, new DateOnly(year, 1, 1), new DateOnly(year, 12, 31), cancellationToken).ConfigureAwait(false)
            : [];
        return new PeopleResult(PeopleOutcome.Done, Body: new
        {
            companyId,
            calendarId = calendar,
            year,
            hijriOffsetDays = offset,
            holidays = holidays.Select(h => View(h, offset)),
        });
    }

    public async Task<PeopleResult> ForEmploymentAsync(Guid employmentId, int year, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(HolidayPermissions.HolidaysRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (year is < 1900 or > 2200)
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["year"] = [YearInvalid] });
        }

        var employment = await FindAsync(employmentId, cancellationToken).ConfigureAwait(false);
        if (employment is null)
        {
            return PeopleResult.NotFound;
        }

        return new PeopleResult(PeopleOutcome.Done, Body: await BodyAsync(employment, year, cancellationToken).ConfigureAwait(false));
    }

    public async Task<PeopleResult> ChooseAsync(Guid employmentId, OptionalHolidayRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(HolidayPermissions.OptionalHolidaysChoose, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var errors = new Dictionary<string, string[]>();
        var year = request.Year ?? 0;
        if (year is < 1900 or > 2200)
        {
            errors["year"] = [YearInvalid];
        }

        if (request.HolidayIds is null)
        {
            errors["holidayIds"] = ["errors.data.required"];
        }

        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        var employment = await FindAsync(employmentId, cancellationToken).ConfigureAwait(false);
        if (employment is null)
        {
            return PeopleResult.NotFound;
        }

        var wanted = request.HolidayIds!.Distinct().ToList();
        var calendar = await CalendarAsync(employment, cancellationToken).ConfigureAwait(false);
        var found = wanted.Count == 0 ? [] : await calendars.FindAsync(wanted, cancellationToken).ConfigureAwait(false);
        var byId = found.ToDictionary(h => h.Id);
        var holidayErrors = new List<string>();
        foreach (var id in wanted)
        {
            if (!byId.TryGetValue(id, out var h) || h.CalendarId != calendar || h.Date.Year != request.Year)
            {
                holidayErrors.Add(NotInCalendar);
            }
            else if (!string.Equals(h.Kind, OptionalKind, StringComparison.Ordinal))
            {
                holidayErrors.Add(NotOptional);
            }
            else if (h.Date < employment.JoiningDate || (employment.SeparationDate is { } end && h.Date > end))
            {
                holidayErrors.Add(OutsideEmployment);
            }
        }

        if (holidayErrors.Count > 0)
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["holidayIds"] = [.. holidayErrors.Distinct(StringComparer.Ordinal)] });
        }

        var limit = await settings.IntAsync(HrmSettingKeys.HrmAttOptionalHolidaysPerYear, employment.CompanyId, 0, cancellationToken).ConfigureAwait(false);
        if (wanted.Count > limit)
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["holidayIds"] = [LimitExceeded] });
        }

        var cutoff = await settings.IntAsync(HrmSettingKeys.HrmAttOptionalHolidayCutoffDays, employment.CompanyId, 0, cancellationToken).ConfigureAwait(false);
        var existing = await db.Set<OptionalHolidayChoice>().Where(c => c.EmploymentId == employmentId && c.HolidayYear == year).ToListAsync(cancellationToken).ConfigureAwait(false);
        var keep = existing.Where(c => wanted.Contains(c.HolidayId)).ToList();
        var remove = existing.Except(keep).ToList();
        var add = wanted.Where(id => existing.All(c => c.HolidayId != id)).ToList();

        // Each change is judged on its own holiday: a choice already made stays even when its cut-off passed, a new or removed one must be early enough.
        var changedIds = add.Concat(remove.Select(r => r.HolidayId)).ToList();
        var changed = changedIds.Count == 0 ? [] : await calendars.FindAsync(changedIds, cancellationToken).ConfigureAwait(false);
        if (changed.Any(h => Today > h.Date.AddDays(-cutoff)))
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["holidayIds"] = [CutoffPassed] });
        }

        var now = clock.UtcNow;
        db.Set<OptionalHolidayChoice>().RemoveRange(remove);
        foreach (var id in add)
        {
            db.Set<OptionalHolidayChoice>().Add(new OptionalHolidayChoice
            {
                Id = Guid.CreateVersion7(now),
                CompanyId = employment.CompanyId,
                EmploymentId = employmentId,
                HolidayId = id,
                HolidayYear = year,
                ChosenAt = now,
                CreatedAt = now,
                CreatedBy = actor.UserId,
                UpdatedAt = now,
                UpdatedBy = actor.UserId,
                RowVersion = 1,
            });
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return PeopleResult.Conflict(ChoiceConflict);
        }

        return new PeopleResult(PeopleOutcome.Done, Body: await BodyAsync(employment, year, cancellationToken).ConfigureAwait(false));
    }

    private async Task<Employment?> FindAsync(Guid employmentId, CancellationToken cancellationToken)
    {
        var employment = await db.Set<Employment>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employmentId, cancellationToken).ConfigureAwait(false);
        return employment is null || !await scope.AllowsAsync(employment, EmploymentAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false) ? null : employment;
    }

    /// <summary>The calendar of the employment: its location's (the assignment in force today, else on the joining day) or the company's.</summary>
    private async Task<Guid?> CalendarAsync(Employment employment, CancellationToken cancellationToken)
    {
        var day = Today < employment.JoiningDate ? employment.JoiningDate : Today;
        var location = await db.Set<EmploymentAssignment>().AsNoTracking()
            .Where(a => a.EmploymentId == employment.Id && a.ValidFrom <= day && (a.ValidTo == null || a.ValidTo > day))
            .Select(a => a.LocationId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return await calendars.CalendarOfAsync(employment.CompanyId, location, cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> BodyAsync(Employment employment, int year, CancellationToken cancellationToken)
    {
        var calendar = await CalendarAsync(employment, cancellationToken).ConfigureAwait(false);
        var offset = await HijriOffsetAsync(employment.CompanyId, cancellationToken).ConfigureAwait(false);
        var limit = await settings.IntAsync(HrmSettingKeys.HrmAttOptionalHolidaysPerYear, employment.CompanyId, 0, cancellationToken).ConfigureAwait(false);
        var cutoff = await settings.IntAsync(HrmSettingKeys.HrmAttOptionalHolidayCutoffDays, employment.CompanyId, 0, cancellationToken).ConfigureAwait(false);
        var holidays = calendar is { } id
            ? await calendars.HolidaysAsync(id, new DateOnly(year, 1, 1), new DateOnly(year, 12, 31), cancellationToken).ConfigureAwait(false)
            : [];
        var chosen = (await db.Set<OptionalHolidayChoice>().AsNoTracking().Where(c => c.EmploymentId == employment.Id && c.HolidayYear == year)
            .Select(c => c.HolidayId).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var today = Today;
        return new
        {
            employmentId = employment.Id,
            calendarId = calendar,
            year,
            hijriOffsetDays = offset,
            optionalLimit = limit,
            optionalChosen = chosen.Count,
            cutoffDays = cutoff,
            holidays = holidays.Select(h => new ChoiceRow(View(h, offset), chosen.Contains(h.Id),
                string.Equals(h.Kind, OptionalKind, StringComparison.Ordinal) && today <= h.Date.AddDays(-cutoff))),
        };
    }

    private async Task<int> HijriOffsetAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var offset = await settings.IntAsync(HrmSettingKeys.HrmAttHijriOffsetDays, companyId, 0, cancellationToken).ConfigureAwait(false);
        return HijriDates.ValidOffset(offset) ? offset : 0;
    }

    internal static HolidayView View(HolidayInfo h, int offset) =>
        new(h.Id, h.CalendarId, h.Date, h.Name, h.Kind, h.IsTentative, string.Equals(h.Kind, OptionalKind, StringComparison.Ordinal), HijriDates.Of(h.Date, offset));

    private sealed record ChoiceRow(HolidayView Holiday, bool Chosen, bool Open);
}
