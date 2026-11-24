using System.Text.Json;
using System.Text.RegularExpressions;
using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Domain.Time;
using Erp.BuildingBlocks.Infrastructure.Persistence;
using Erp.Hrm.Application.Attendance;
using Erp.Hrm.Application.Generated;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts.Attendance;
using Erp.Hrm.Domain.Generated;
using Erp.Hrm.Infrastructure.Employments;
using Erp.Hrm.Infrastructure.Generated;
using Erp.Hrm.Infrastructure.People;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Erp.Hrm.Infrastructure.Attendance;

/// <summary>
/// Shifts (slice S1.28a Build items 1 to 3; FR-HRM-ATT-06). The generated writes of <c>hrm.shifts</c> are closed: this service checks the
/// times of each of the six types (pure rules in <see cref="ShiftRules"/>) and keeps history. A change of times, type or segments is a new
/// version from a named day: the version in force ends the day before and the new row starts, so published rosters and past days keep
/// the row they used. A change of the name or the active flag edits the row in place. The weekly-off pattern, grace minutes and minimum
/// rest are not on the shift: they come from the <c>shift</c> policy (ruling Q2, Q3), read through <c>IPolicyResolver</c>.
/// </summary>
internal sealed partial class ShiftService(
    HrmDbContext db,
    IClock clock,
    ICurrentActor actor,
    IPermissionChecker permissions,
    IDataScopeFilter scope,
    ShiftPolicyReader policies) : IShiftService
{
    public const string CodeRequired = "errors.hrm.shift.code_required";
    public const string CodeTaken = "errors.hrm.shift.code_taken";
    public const string NameRequired = "errors.hrm.shift.name_required";
    public const string PolicyUnknown = "errors.hrm.shift.policy_unknown";
    public const string EffectiveFromRequired = "errors.hrm.shift.effective_from_required";
    public const string EffectiveFromOrder = "errors.hrm.shift.effective_from_order";
    public const string NotCurrent = "errors.hrm.shift.not_current_version";
    public const string PeriodTooLong = "errors.hrm.shift_pattern.period_too_long";

    private const int CodeLength = 40;
    private const int NameLength = 200;

    private DbContextScopeSource Source => new(db);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]*$")]
    private static partial Regex CodePattern();

    public async Task<PeopleResult> CreateAsync(ShiftRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(ShiftPermissions.ShiftsCreate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (request.CompanyId is not { } company)
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["companyId"] = [PersonService.Required] });
        }

        var now = clock.UtcNow;
        var row = new Shift
        {
            Id = Guid.CreateVersion7(now),
            CompanyId = company,
            NameI18n = JsonDocument.Parse("{}"),
            Segments = JsonDocument.Parse("[]"),
            IsActive = request.IsActive ?? true,
            CreatedAt = now,
            CreatedBy = actor.UserId,
            UpdatedAt = now,
            UpdatedBy = actor.UserId,
            RowVersion = 1,
        };
        if (!await scope.AllowsAsync(row, ShiftAccess.Scope, "create", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        Apply(row, request, creating: true, errors);
        if (errors.Count > 0)
        {
            return PeopleResult.Fields(errors);
        }

        db.Add(row);
        return await SaveAsync(row, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeopleResult> UpdateAsync(Guid shiftId, ShiftRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await permissions.HasPermissionAsync(ShiftPermissions.ShiftsUpdate, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        if (request.RowVersion is not { } expected)
        {
            return PeopleResult.Invalid(PersonService.RowVersionRequired);
        }

        var row = await db.Set<Shift>().FirstOrDefaultAsync(s => s.Id == shiftId && !s.IsDeleted, cancellationToken).ConfigureAwait(false);
        if (row is null || !await scope.AllowsAsync(row, ShiftAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        if (!await scope.AllowsAsync(row, ShiftAccess.Scope, "update", Source, cancellationToken).ConfigureAwait(false))
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Forbidden(AccessDeniedException.OutOfScope);
        }

        if (row.RowVersion != expected)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(PersonService.Concurrency);
        }

        if (row.ValidTo is not null)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(NotCurrent);
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.CompanyId is { } other && other != row.CompanyId)
        {
            errors["companyId"] = [PersonService.CannotClear];
        }

        // Work on a copy: a change of times or type becomes a new version, the row in force only gets its end date.
        var before = Fingerprint(row);
        var next = Copy(row);
        Apply(next, request, creating: false, errors);
        var timesChanged = Fingerprint(next) != before;
        var onlyLabels = !timesChanged;
        if (errors.Count == 0 && !onlyLabels)
        {
            if (request.EffectiveFrom is not { } from)
            {
                errors["effectiveFrom"] = [EffectiveFromRequired];
            }
            else if (row.ValidFrom is { } start && from <= start)
            {
                errors["effectiveFrom"] = [EffectiveFromOrder];
            }
        }

        if (errors.Count > 0)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Fields(errors);
        }

        var now = clock.UtcNow;
        if (onlyLabels)
        {
            row.NameI18n = next.NameI18n;
            row.IsActive = next.IsActive;
            EmploymentApplier.Bump(row, now, actor.UserId);
            return await SaveAsync(row, null, cancellationToken).ConfigureAwait(false);
        }

        next.Id = Guid.CreateVersion7(now);
        next.PreviousShiftId = row.Id;
        next.ValidFrom = request.EffectiveFrom;
        next.ValidTo = null;
        next.CreatedAt = now;
        next.CreatedBy = actor.UserId;
        next.UpdatedAt = now;
        next.UpdatedBy = actor.UserId;
        next.RowVersion = 1;
        row.ValidTo = request.EffectiveFrom!.Value.AddDays(-1);
        EmploymentApplier.Bump(row, now, actor.UserId);
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false); // the old row first: the code is unique among open-ended versions
            db.Add(next);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(PersonService.Concurrency);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            db.ChangeTracker.Clear();
            return Refusal(pg);
        }

        return new PeopleResult(PeopleOutcome.Done, Body: View(next, row.Id));
    }

    public async Task<PeopleResult> WeeklyOffAsync(Guid employmentId, DateOnly? firstDay, DateOnly? lastDay, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(ShiftPermissions.WeeklyOffRead, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.Forbidden();
        }

        var employment = await db.Set<Employment>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employmentId, cancellationToken).ConfigureAwait(false);
        if (employment is null || !await scope.AllowsAsync(employment, EmploymentAccess.Scope, "read", Source, cancellationToken).ConfigureAwait(false))
        {
            return PeopleResult.NotFound;
        }

        var start = firstDay ?? employment.JoiningDate;
        var end = lastDay ?? start.AddDays(30);
        if (end < start)
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["to"] = [PersonService.DateOrder] });
        }

        if (end.DayNumber - start.DayNumber >= WeeklyOffPattern.MaxDays)
        {
            return PeopleResult.Fields(new Dictionary<string, string[]> { ["to"] = [PeriodTooLong] });
        }

        var policy = await policies.ReadAsync(employmentId, start, end, cancellationToken).ConfigureAwait(false);
        if (policy is null)
        {
            return PeopleResult.NotFound;
        }

        var days = new List<string>();
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            if (policy.For(d).IsOff(d))
            {
                days.Add(d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        var whole = policy.For(start);
        return new PeopleResult(PeopleOutcome.Done, Body: new
        {
            employmentId,
            from = start,
            to = end,
            offDays = days,
            policy = whole.PolicyCode,
            reason = whole.PolicyCode is null ? whole.NoPolicyReason : null,
            graceMinutes = whole.GraceMinutes,
            minimumRestMinutes = whole.MinimumRestMinutes,
            patternError = whole.Error,
        });
    }

    private static string Fingerprint(Shift s) =>
        string.Join('|', s.ShiftType, s.StartTime, s.EndTime, s.CoreStart, s.CoreEnd, s.CrossesMidnight, s.Segments.RootElement.GetRawText(), s.MinFullDayMinutes, s.PolicyId);

    private static Shift Copy(Shift s) => new()
    {
        CompanyId = s.CompanyId, Code = s.Code, NameI18n = s.NameI18n, ShiftType = s.ShiftType, StartTime = s.StartTime, EndTime = s.EndTime, CoreStart = s.CoreStart, CoreEnd = s.CoreEnd,
        CrossesMidnight = s.CrossesMidnight, Segments = s.Segments, MinFullDayMinutes = s.MinFullDayMinutes, PolicyId = s.PolicyId, IsActive = s.IsActive,
    };

    /// <summary>Copies the request into the row and checks the result; field errors are collected.</summary>
    private static void Apply(Shift row, ShiftRequest r, bool creating, Dictionary<string, string[]> errors)
    {
        void Fail(string field, string key) => errors[field] = [.. errors.GetValueOrDefault(field, []).Append(key).Distinct(StringComparer.Ordinal)];
        var clear = (r.Clear ?? []).ToHashSet(StringComparer.Ordinal);
        foreach (var name in clear.Where(c => !ShiftRequest.Clearable.Contains(c, StringComparer.Ordinal)))
        {
            Fail("clear", PersonService.CannotClear);
        }

        if (creating || r.Code is not null)
        {
            var code = r.Code?.Trim();
            if (string.IsNullOrEmpty(code) || !CodePattern().IsMatch(code))
            {
                Fail("code", CodeRequired);
            }
            else if (code.Length > CodeLength)
            {
                Fail("code", PersonService.TooLong);
            }
            else if (!creating && !string.Equals(code, row.Code, StringComparison.OrdinalIgnoreCase))
            {
                Fail("code", PersonService.CannotClear); // the code names the shift across its versions and never changes
            }
            else
            {
                row.Code = code;
            }
        }

        if (creating || r.NameI18n is not null)
        {
            var names = (r.NameI18n ?? new Dictionary<string, string>()).Select(n => (Key: n.Key.Trim().ToLowerInvariant(), Value: n.Value?.Trim() ?? string.Empty)).Where(n => n.Value.Length > 0).ToList();
            if (names.Count == 0 || names.Any(n => n.Key.Length is < 2 or > 5))
            {
                Fail("nameI18n", NameRequired);
            }
            else if (names.Any(n => n.Value.Length > NameLength))
            {
                Fail("nameI18n", PersonService.TooLong);
            }
            else
            {
                row.NameI18n = JsonSerializer.SerializeToDocument(names.DistinctBy(n => n.Key).ToDictionary(n => n.Key, n => n.Value));
            }
        }

        if (r.ShiftType is not null)
        {
            row.ShiftType = r.ShiftType;
        }

        row.StartTime = r.StartTime ?? row.StartTime;
        row.EndTime = r.EndTime ?? row.EndTime;
        row.CoreStart = clear.Contains("coreStart") ? null : r.CoreStart ?? row.CoreStart;
        row.CoreEnd = clear.Contains("coreEnd") ? null : r.CoreEnd ?? row.CoreEnd;
        row.CrossesMidnight = r.CrossesMidnight ?? row.CrossesMidnight;
        row.MinFullDayMinutes = r.MinFullDayMinutes ?? row.MinFullDayMinutes;
        row.PolicyId = clear.Contains("policyId") ? null : r.PolicyId ?? row.PolicyId;
        row.IsActive = r.IsActive ?? row.IsActive;
        IReadOnlyList<ShiftSegment>? segments = ShiftRules.ParseSegments(r.Segments ?? JsonSerializer.Deserialize<JsonElement>(row.Segments.RootElement.GetRawText()));
        if (segments is null)
        {
            Fail("segments", ShiftRules.SegmentsInvalid);
            segments = [];
        }
        else if (r.Segments is not null)
        {
            row.Segments = JsonSerializer.SerializeToDocument(ShiftRules.SegmentsJson(segments));
        }

        if (string.IsNullOrEmpty(row.ShiftType))
        {
            Fail("shiftType", ShiftRules.TypeInvalid);
            return;
        }

        foreach (var (field, keys) in ShiftRules.Validate(new ShiftSpec(row.ShiftType, row.StartTime, row.EndTime, row.CoreStart, row.CoreEnd, row.CrossesMidnight, segments, row.MinFullDayMinutes)))
        {
            errors[field] = keys;
        }
    }

    private async Task<PeopleResult> SaveAsync(Shift row, Guid? previous, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return PeopleResult.Conflict(PersonService.Concurrency);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            db.ChangeTracker.Clear();
            return Refusal(pg);
        }

        return new PeopleResult(previous is null && row.RowVersion == 1 ? PeopleOutcome.Created : PeopleOutcome.Done, Body: View(row, previous));
    }

    internal static Dictionary<string, object?> View(Shift s, Guid? previous = null) => new(StringComparer.Ordinal)
    {
        ["id"] = s.Id,
        ["companyId"] = s.CompanyId,
        ["code"] = s.Code,
        ["nameI18n"] = JsonSerializer.Deserialize<JsonElement>(s.NameI18n.RootElement.GetRawText()),
        ["shiftType"] = s.ShiftType,
        ["startTime"] = s.StartTime,
        ["endTime"] = s.EndTime,
        ["coreStart"] = s.CoreStart,
        ["coreEnd"] = s.CoreEnd,
        ["crossesMidnight"] = s.CrossesMidnight,
        ["segments"] = JsonSerializer.Deserialize<JsonElement>(s.Segments.RootElement.GetRawText()),
        ["minFullDayMinutes"] = s.MinFullDayMinutes,
        ["policyId"] = s.PolicyId,
        ["validFrom"] = s.ValidFrom,
        ["validTo"] = s.ValidTo,
        ["previousShiftId"] = s.PreviousShiftId,
        ["isActive"] = s.IsActive,
        ["rowVersion"] = s.RowVersion,
        ["replacedShiftId"] = previous,
    };

    internal static PeopleResult Refusal(PostgresException pg) => pg.ConstraintName switch
    {
        "uq_shifts_company_id_code" => Field("code", CodeTaken),
        "fk_shifts_company_id" => Field("companyId", "errors.data.not_found"),
        "fk_shifts_policy_id" => Field("policyId", PolicyUnknown),
        "ck_shifts_version_dates" => Field("effectiveFrom", EffectiveFromOrder),
        _ => PersonService.Refusal(pg),
    };

    private static PeopleResult Field(string field, string key) => PeopleResult.Fields(new Dictionary<string, string[]> { [field] = [key] });
}
